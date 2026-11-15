#include <sys/types.h>
#include <windows.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include "quiet.h"
#include "horizon_quiet.h"

enum { HC_RATE = 48000, HC_PAYLOAD = 4096, HC_QUEUE = 32,
       HC_MAX_SAMPLES = HC_RATE * 60, HC_LEAD = 2400, HC_TAIL = 9600 };

/* Derived from Quiet's upstream ultrasonic-3600 OFDM profile. Moving the
 * carrier and interpolation places it inside a voice channel. This is a
 * Horizon profile, not a claim that upstream's cable-64k survives voice codecs.
 * The complete JSON is embedded so portable clients never need a data file.
 */
static const char profile[] =
    "{\"horizon-voice-ofdm\":{"
    "\"mod_scheme\":\"qam16\",\"checksum_scheme\":\"crc32\","
    "\"inner_fec_scheme\":\"v27p23\",\"outer_fec_scheme\":\"rs8\","
    "\"frame_length\":4096,"
    "\"modulation\":{\"center_frequency\":1700,\"gain\":0.09},"
    "\"interpolation\":{\"shape\":\"kaiser\",\"samples_per_symbol\":16,"
    "\"symbol_delay\":4,\"excess_bandwidth\":0.33},"
    "\"encoder_filters\":{\"dc_filter_alpha\":0.01},"
    "\"resampler\":{\"delay\":13,\"bandwidth\":0.45,\"attenuation\":60,"
    "\"filter_bank_size\":64},"
    "\"ofdm\":{\"num_subcarriers\":64,\"cyclic_prefix_length\":20,"
    "\"taper_length\":8,\"left_band\":4,\"right_band\":13}}}";
static const char profile_name[] = "horizon-voice-ofdm";

typedef struct {
    quiet_decoder *decoder;
    int head, queued;
    int sizes[HC_QUEUE];
    uint8_t packets[HC_QUEUE][HC_PAYLOAD];
    uint8_t scratch[65536];
} hc_decoder;

/* Quiet's non-pthread error slot and libfec's lookup initialization are global.
 * One Windows SRW lock covers every call into these libraries, including encode
 * sizing. This keeps independent handles safe without a pthread runtime DLL.
 */
static SRWLOCK library_lock = SRWLOCK_INIT;

const char *hc_name(void) { return "Quiet / Horizon voice-band OFDM"; }
int hc_max_payload(int mode) { return mode == 0 ? HC_PAYLOAD : -1; }

void *hc_create(int mode, int sample_rate) {
    if (mode != 0 || sample_rate != HC_RATE) return NULL;
    hc_decoder *state = calloc(1, sizeof(*state));
    if (!state) return NULL;
    AcquireSRWLockExclusive(&library_lock);
    quiet_decoder_options *options = quiet_decoder_profile_str(profile, profile_name);
    if (options) {
        state->decoder = quiet_decoder_create(options, (float)HC_RATE);
        free(options);
    }
    if (state->decoder) quiet_decoder_set_nonblocking(state->decoder);
    ReleaseSRWLockExclusive(&library_lock);
    if (!state->decoder) { free(state); return NULL; }
    return state;
}

void hc_destroy(void *handle) {
    if (!handle) return;
    hc_decoder *state = handle;
    AcquireSRWLockExclusive(&library_lock);
    quiet_decoder_destroy(state->decoder);
    free(state);
    ReleaseSRWLockExclusive(&library_lock);
}

int hc_encode(int mode, const uint8_t *data, int length, float *out, int capacity) {
    if (mode != 0 || !data || length < 1 || length > HC_PAYLOAD || capacity < 0 ||
        (!out && capacity != 0)) return -1;
    int result = -3;
    quiet_encoder *encoder = NULL;
    /* A finite bound applies even if a damaged dependency stops making progress. */
    float *wave = calloc(HC_MAX_SAMPLES, sizeof(float));
    if (!wave) return -3;
    AcquireSRWLockExclusive(&library_lock);
    quiet_encoder_options *options = quiet_encoder_profile_str(profile, profile_name);
    if (!options) goto cleanup;
    encoder = quiet_encoder_create(options, (float)HC_RATE);
    free(options);
    if (!encoder) goto cleanup;
    quiet_encoder_set_nonblocking(encoder);
    if (quiet_encoder_send(encoder, data, (size_t)length) != length) goto cleanup;
    quiet_encoder_close(encoder);
    int used = HC_LEAD;
    for (;;) {
        if (used + 4096 + HC_TAIL > HC_MAX_SAMPLES) goto cleanup;
        ssize_t count = quiet_encoder_emit(encoder, wave + used, 4096);
        if (count < 0) goto cleanup;
        if (count == 0) break;
        used += (int)count;
    }
    used += HC_TAIL;
    float peak = 0.0f;
    for (int i = HC_LEAD; i < used - HC_TAIL; ++i) {
        if (!isfinite(wave[i])) goto cleanup;
        float value = fabsf(wave[i]);
        if (value > peak) peak = value;
    }
    if (peak < 1e-8f) goto cleanup;
    if (out) {
        if (capacity < used) { result = -2; goto cleanup; }
        float gain = 0.75f / peak;
        for (int i = 0; i < used; ++i) out[i] = wave[i] * gain;
    }
    result = used;
cleanup:
    quiet_encoder_destroy(encoder);
    ReleaseSRWLockExclusive(&library_lock);
    free(wave);
    return result;
}

static int drain(hc_decoder *state) {
    for (;;) {
        ssize_t size = quiet_decoder_recv(state->decoder, state->scratch, sizeof(state->scratch));
        if (size < 0) return quiet_get_last_error() == quiet_would_block ? 0 : -3;
        if (size == 0) return 0;
        if (size > HC_PAYLOAD) continue;
        if (state->queued == HC_QUEUE) {
            /* Real-time audio must stay bounded if the consumer falls behind. */
            state->head = (state->head + 1) % HC_QUEUE;
            state->queued--;
        }
        int index = (state->head + state->queued) % HC_QUEUE;
        memcpy(state->packets[index], state->scratch, (size_t)size);
        state->sizes[index] = (int)size;
        state->queued++;
    }
}

int hc_feed(void *handle, const float *mono, int count) {
    if (!handle || count < 0 || count > HC_RATE * 5 || (!mono && count)) return -1;
    for (int i = 0; i < count; ++i) if (!isfinite(mono[i])) return -1;
    hc_decoder *state = handle;
    int result = 0;
    AcquireSRWLockExclusive(&library_lock);
    /* Drain frequently: the upstream receive ring is also finite. */
    for (int offset = 0; offset < count;) {
        int block = count - offset;
        if (block > 1024) block = 1024;
        if (quiet_decoder_consume(state->decoder, mono + offset, (size_t)block) != block) {
            result = -3; break;
        }
        result = drain(state);
        if (result < 0) break;
        offset += block;
    }
    ReleaseSRWLockExclusive(&library_lock);
    return result;
}

int hc_receive(void *handle, uint8_t *out, int capacity) {
    if (!handle || capacity < 0 || (!out && capacity)) return -1;
    hc_decoder *state = handle;
    int result = 0;
    AcquireSRWLockExclusive(&library_lock);
    if (state->queued) {
        result = state->sizes[state->head];
        if (!out || capacity < result) result = -2;
        else {
            memcpy(out, state->packets[state->head], (size_t)result);
            state->head = (state->head + 1) % HC_QUEUE;
            state->queued--;
        }
    }
    ReleaseSRWLockExclusive(&library_lock);
    return result;
}
