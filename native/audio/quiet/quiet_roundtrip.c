#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "horizon_quiet.h"

static int roundtrip(int length, int altered) {
    uint8_t input[4096], output[4096];
    for (int i = 0; i < length; ++i) input[i] = (uint8_t)((i * 73 + i / 7) ^ 0xA5);
    int required = hc_encode(0, input, length, NULL, 0);
    if (required <= 0 || required > 48000 * 60) return 1;
    float *samples = calloc((size_t)required, sizeof(float));
    if (!samples) return 2;
    int actual = hc_encode(0, input, length, samples, required);
    if (actual != required || hc_encode(0, input, length, samples, required - 1) != -2) return 3;
    void *decoder = hc_create(0, 48000);
    if (!decoder) return 4;
    for (int i = 0; i < actual; ++i) {
        if (!isfinite(samples[i]) || fabsf(samples[i]) > 0.751f) return 5;
        if (altered) {
            /* Deterministic noise, gain change and PCM16 quantization. */
            float noise = (((i * 17) % 101) - 50) * 0.00001f;
            samples[i] = roundf((samples[i] * 0.45f + noise) * 32767.0f) / 32767.0f;
        }
    }
    for (int offset = 0; offset < actual;) {
        int count = 137 + offset % 1500;
        if (count > actual - offset) count = actual - offset;
        if (hc_feed(decoder, samples + offset, count) != 0) return 6;
        offset += count;
    }
    if (hc_receive(decoder, output, length - 1) != -2) return 7;
    int received = hc_receive(decoder, output, sizeof(output));
    int failed = received != length || memcmp(input, output, (size_t)length) != 0;
    if (hc_receive(decoder, output, sizeof(output)) != 0) failed = 1;
    printf("Quiet %d bytes: %d samples, %.3f s, %.1f payload bit/s, %s, %s\n",
        length, actual, actual / 48000.0, length * 8.0 * 48000.0 / actual,
        altered ? "PCM16/gain/noise" : "clean", failed ? "FAIL" : "PASS");
    hc_destroy(decoder);
    free(samples);
    return failed ? 8 : 0;
}

static int bounded_queue(void) {
    const uint8_t input[] = {0x48, 0x43, 0xFF, 0x00, 0x3A};
    uint8_t output[4096];
    int count = hc_encode(0, input, sizeof(input), NULL, 0);
    if (count < 0) return 11;
    float *samples = calloc((size_t)count, sizeof(float));
    void *decoder = hc_create(0, 48000);
    if (!samples || !decoder || hc_encode(0, input, sizeof(input), samples, count) != count) return 12;
    float invalid = NAN;
    if (hc_feed(decoder, &invalid, 1) >= 0 || hc_feed(decoder, NULL, 1) >= 0) return 13;
    for (int packet = 0; packet < 40; ++packet) {
        for (int offset = 0; offset < count;) {
            int block = count - offset;
            if (block > 2048) block = 2048;
            if (hc_feed(decoder, samples + offset, block) != 0) return 14;
            offset += block;
        }
    }
    int queued = 0, received;
    while ((received = hc_receive(decoder, output, sizeof(output))) > 0) {
        if (received != sizeof(input) || memcmp(output, input, sizeof(input))) return 15;
        ++queued;
    }
    hc_destroy(decoder);
    free(samples);
    if (received < 0 || queued != 32) return 16;
    puts("Quiet persistent decoder / 40 frames / 32 packet queue / invalid PCM: PASS");
    return 0;
}

int main(void) {
    if (hc_max_payload(0) != 4096 || hc_max_payload(1) >= 0 || hc_create(0, 44100) ||
        hc_encode(0, NULL, 0, NULL, 0) >= 0) return 10;
    const int lengths[] = {1, 64, 1024, 4096};
    for (unsigned i = 0; i < sizeof(lengths) / sizeof(lengths[0]); ++i) {
        for (int altered = 0; altered <= 1; ++altered) {
            int result = roundtrip(lengths[i], altered);
            if (result) { fprintf(stderr, "Roundtrip failed (%d)\n", result); return result; }
        }
    }
    return bounded_queue();
}
