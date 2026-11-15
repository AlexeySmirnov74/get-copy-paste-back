#include "aicodix.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

static void require(bool condition, const char *message) {
    if (!condition) throw std::runtime_error(message);
}
int main() {
    try {
        require(!hc_create(0, 44100), "unsupported sample rate accepted");
        require(!hc_create(2, 48000), "unsupported mode accepted");
        require(hc_max_payload(2) < 0, "unsupported max-payload mode accepted");
        for (int mode = 0; mode < 2; ++mode) {
            void *rx = hc_create(mode, 48000);
            require(rx != nullptr, "decoder creation failed");
            const int maximum = hc_max_payload(mode);
            uint32_t random = 0x5638aaf1;
            for (int iteration = 0; iteration < 3; ++iteration) {
                const int length = iteration == 0 ? 1 : iteration == 1 ? maximum : 257;
                std::vector<uint8_t> payload(length);
                for (int i = 0; i < length; ++i) {
                    random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                    payload[i] = uint8_t(random);
                }
                const int count = hc_encode(mode, payload.data(), length, nullptr, 0);
                require(count > 0, "encode sizing failed");
                std::vector<float> pcm(count);
                require(hc_encode(mode, payload.data(), length, pcm.data(), count - 1) == -2, "encode capacity check failed");
                const auto started = std::chrono::steady_clock::now();
                require(hc_encode(mode, payload.data(), length, pcm.data(), count) == count, "encoding failed");
                const double encode_seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
                // Exercise normalized, attenuated, quantized audio and arbitrary
                // capture block boundaries across successive frames on one stream.
                for (int i = 0; i < count; ++i) {
                    require(std::isfinite(pcm[i]) && std::abs(pcm[i]) <= 1.01f, "invalid PCM");
                    pcm[i] = std::round(pcm[i] * 0.67f * 32767.0f) / 32767.0f;
                }
                const int chunks[] = {1, 127, 960, 13, 2048, 333};
                int position = 0, chunk = 0;
                while (position < count) {
                    int take = std::min(chunks[chunk++ % 6], count - position);
                    require(hc_feed(rx, pcm.data() + position, take) == 0, "stream feed failed");
                    position += take;
                }
                // A realtime capture keeps running after the playback completes.
                // This flushes the decoder's roughly three-symbol tracking delay.
                std::vector<float> tail(24000, 0.0f);
                require(hc_feed(rx, tail.data(), int(tail.size())) == 0, "tail feed failed");
                std::vector<uint8_t> decoded(maximum);
                require(hc_receive(rx, decoded.data(), length - 1) == -2, "receive capacity check failed or no decoded frame");
                int received = hc_receive(rx, decoded.data(), maximum);
                require(received == length && std::equal(payload.begin(), payload.end(), decoded.begin()), "decoded payload differs");
                require(hc_receive(rx, decoded.data(), maximum) == 0, "unexpected duplicate frame");
                const double seconds = count / 48000.0;
                const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
                std::cout << "mode=" << mode << " bytes=" << length << " samples=" << count
                    << " audio_seconds=" << seconds << " useful_bps=" << (8.0 * length / seconds)
                    << " encode_seconds=" << encode_seconds
                    << " encode_decode_seconds=" << elapsed << std::endl;
            }
            hc_destroy(rx);

            // Priority sends may play several encoded frames directly adjacent.
            // Receive while feeding and flush once, after the final frame only.
            rx = hc_create(mode, 48000);
            const std::vector<uint8_t> message = {0x00, 0xff, 0x81, 0x00, 0x42};
            const int samples = hc_encode(mode, message.data(), int(message.size()), nullptr, 0);
            std::vector<float> adjacent(samples);
            require(hc_encode(mode, message.data(), int(message.size()), adjacent.data(), samples) == samples, "adjacent encoding failed");
            int received_count = 0;
            for (int repeat = 0; repeat < 3; ++repeat) {
                for (int offset = 0; offset < samples; offset += 960) {
                    const int take = std::min(960, samples - offset);
                    require(hc_feed(rx, adjacent.data() + offset, take) == 0, "adjacent frame feed failed");
                    std::vector<uint8_t> decoded(maximum);
                    const int received = hc_receive(rx, decoded.data(), maximum);
                    if (received) {
                        require(received == int(message.size()) && std::equal(message.begin(), message.end(), decoded.begin()), "adjacent payload differs");
                        ++received_count;
                    }
                }
                if (repeat == 1) require(received_count >= 1, "first adjacent frame not delivered during second playback");
            }
            std::vector<float> final_tail(24000, 0.0f);
            require(hc_feed(rx, final_tail.data(), int(final_tail.size())) == 0, "adjacent final tail feed failed");
            std::vector<uint8_t> decoded(maximum);
            for (int received; (received = hc_receive(rx, decoded.data(), maximum)) != 0;) {
                require(received == int(message.size()) && std::equal(message.begin(), message.end(), decoded.begin()), "adjacent final payload differs");
                ++received_count;
            }
            require(received_count == 3, "back-to-back frame count differs");
            hc_destroy(rx);
            std::cout << "mode=" << mode << " back_to_back_frames=3 passed" << std::endl;

            if (mode == 0) {
                rx = hc_create(mode, 48000);
                bool overflow = false;
                for (int repeat = 0; repeat < 35; ++repeat) {
                    const int status = hc_feed(rx, adjacent.data(), samples);
                    require(status == 0 || status == -4, "queue stress feed failed");
                    overflow = overflow || status == -4;
                }
                const int status = hc_feed(rx, final_tail.data(), int(final_tail.size()));
                require(status == 0 || status == -4, "queue stress final tail failed");
                overflow = overflow || status == -4;
                int queued = 0;
                for (int received; (received = hc_receive(rx, decoded.data(), maximum)) != 0;) {
                    require(received == int(message.size()), "queue stress payload size differs");
                    ++queued;
                }
                require(overflow && queued == 32, "receive queue did not remain bounded at 32");
                hc_destroy(rx);
                std::cout << "bounded_queue_frames=35 retained=32 overflow=-4 passed" << std::endl;
            }
        }
        std::cout << "aicodix native chunked-PCM roundtrip passed" << std::endl;
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "aicodix native roundtrip failed: " << error.what() << std::endl;
        return 1;
    }
}
