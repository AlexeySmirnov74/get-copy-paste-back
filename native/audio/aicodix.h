#pragma once
#include <stdint.h>

#if defined(_WIN32)
# if defined(HC_BUILD)
#  define HC_API __declspec(dllexport)
# else
#  define HC_API __declspec(dllimport)
# endif
#else
# define HC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif
/* Mono normalized float PCM, 48000 samples/sec. Modes: 0 QAM16, 1 QAM256.
   Each decoder belongs to one stream and must be externally serialized.
   Negative results: -1 invalid argument, -2 insufficient output capacity,
   -3 allocation/internal error, -4 receive queue full (oldest frame discarded).
   Capacity errors leave the queued message available for another receive. */
HC_API void *hc_create(int mode, int sample_rate);
HC_API void hc_destroy(void *handle);
HC_API int hc_max_payload(int mode);
/* A null output with capacity zero returns required float sample count.
   Zero-length messages are unsupported because receive uses zero for no data. */
HC_API int hc_encode(int mode, const uint8_t *data, int length, float *out, int capacity);
HC_API int hc_feed(void *handle, const float *mono, int count);
HC_API int hc_receive(void *handle, uint8_t *out, int capacity);
#ifdef __cplusplus
}
#endif
