// Streaming C ABI for the pinned 0BSD aicodix modem.
#define HC_BUILD
#include "aicodix.h"
#include <array>
#include <deque>
#include <memory>
#include <vector>
#include <cmath>
#include <cstring>
#include "encoder.hh"
#include "decoder.hh"

namespace {
constexpr int sample_rate = 48000;
constexpr int envelope_size = 8;
constexpr int padding_samples = sample_rate / 10;
constexpr size_t queue_limit = 32;
using Complex = DSP::Complex<float>;
using Tx = Encoder<float, Complex, sample_rate>;
using Rx = Decoder<float, Complex, sample_rate>;

int upstream_mode(int mode) {
    // QAM16 / QAM256, rate 3/4, normal polar-code frame.
    return mode == 0 ? 0x35 : mode == 1 ? 0x55 : -1;
}
int64_t call_sign() {
    int64_t value = 0;
    for (const char *p = "HORIZON"; *p; ++p)
        value = value * 40 + *p - 'A' + 14;
    return value;
}
int frame_capacity(int mode) {
    Common common;
    return upstream_mode(mode) >= 0 && common.setup(upstream_mode(mode))
        ? common.data_bytes - envelope_size : -1;
}
int sample_count(int mode) {
    Common common;
    if (upstream_mode(mode) < 0 || !common.setup(upstream_mode(mode))) return -1;
    // start emits leading noise; next emits two sync symbols, metadata, and
    // payload; stop emits its final guard. Include a 100ms margin at both ends.
    return 2 * padding_samples + (common.symbol_count + 4) * (Tx::symbol_len + Tx::guard_len);
}
struct Receiver {
    int mode;
    std::unique_ptr<Rx> decoder;
    std::deque<std::vector<uint8_t>> messages;
    explicit Receiver(int m) : mode(m), decoder(new Rx()) {}
    bool push_frame() {
        Rx &rx = *decoder;
        if (rx.analog_mode || rx.oper_mode != upstream_mode(mode) ||
            rx.data_bytes < envelope_size || std::memcmp(rx.data, "HCA1", 4)) return true;
        const int length = rx.data[4] | (int(rx.data[5]) << 8);
        const int complement = rx.data[6] | (int(rx.data[7]) << 8);
        if (length < 1 || length > frame_capacity(mode) || (length ^ complement) != 0xffff) return true;
        bool okay = messages.size() < queue_limit;
        if (!okay) messages.pop_front();
        messages.emplace_back(rx.data + envelope_size, rx.data + envelope_size + length);
        return okay;
    }
};
}

extern "C" {
void *hc_create(int mode, int rate) {
    if (upstream_mode(mode) < 0 || rate != sample_rate) return nullptr;
    try { return new Receiver(mode); } catch (...) { return nullptr; }
}
void hc_destroy(void *handle) { delete static_cast<Receiver *>(handle); }
int hc_max_payload(int mode) { return frame_capacity(mode); }
int hc_encode(int mode, const uint8_t *data, int length, float *out, int capacity) {
    const int maximum = frame_capacity(mode);
    if (maximum < 0 || !data || length < 1 || length > maximum || capacity < 0 || (!out && capacity)) return -1;
    const int required = sample_count(mode);
    if (!out) return required;
    if (capacity < required) return -2;
    try {
        std::unique_ptr<Tx> tx(new Tx());
        std::array<uint8_t, Common::data_max> payload{};
        std::memcpy(payload.data(), "HCA1", 4);
        payload[4] = uint8_t(length); payload[5] = uint8_t(length >> 8);
        payload[6] = uint8_t(~length); payload[7] = uint8_t((~length) >> 8);
        std::memcpy(payload.data() + envelope_size, data, length);
        int position = padding_samples;
        std::fill(out, out + padding_samples, 0.0f);
        auto emit = [&]() {
            std::copy(tx->audio, tx->audio + tx->audio_len, out + position);
            position += tx->audio_len;
        };
        if (!tx->start(upstream_mode(mode))) return -3;
        emit();
        if (!tx->begin(payload.data(), length + envelope_size, call_sign())) return -3;
        while (tx->next()) emit();
        tx->stop(); emit();
        std::fill(out + position, out + required, 0.0f);
        return required;
    } catch (...) { return -3; }
}
int hc_feed(void *handle, const float *mono, int count) {
    if (!handle || count < 0 || (!mono && count)) return -1;
    try {
        Receiver &receiver = *static_cast<Receiver *>(handle);
        // Upstream uses this signed counter only for its CLI diagnostic output.
        // Keep a continuously running capture from overflowing it after 12h.
        receiver.decoder->sample_count = 0;
        int status = 0;
        for (int i = 0; i < count; ++i) {
            // Malformed input cannot poison the DSP history with NaN/Infinity.
            const float sample = std::isfinite(mono[i]) ? mono[i] : 0.0f;
            int events = receiver.decoder->feed(sample);
            if ((events & Rx::decoded_frame) && !receiver.push_frame()) status = -4;
        }
        return status;
    } catch (...) { return -3; }
}
int hc_receive(void *handle, uint8_t *out, int capacity) {
    if (!handle || capacity < 0 || (!out && capacity)) return -1;
    try {
        Receiver &receiver = *static_cast<Receiver *>(handle);
        if (receiver.messages.empty()) return 0;
        const std::vector<uint8_t> &message = receiver.messages.front();
        if (!out || capacity < int(message.size())) return -2;
        const int length = int(message.size());
        std::memcpy(out, message.data(), message.size());
        receiver.messages.pop_front();
        return length;
    } catch (...) { return -3; }
}
}
