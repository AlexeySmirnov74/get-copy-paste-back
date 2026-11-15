#ifndef HORIZON_QUIET_H
#define HORIZON_QUIET_H
#include <stdint.h>
#ifdef _WIN32
#define HC_API __declspec(dllexport)
#else
#define HC_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Mode 0 is the fixed Horizon voice-band OFDM profile; PCM is mono float/48000 Hz.
 * Encode: (out=NULL,capacity=0) computes the required sample count. A second
 * call generates the same complete waveform. No caller memory is retained.
 * Receive: 0 means empty; -2 means insufficient capacity (packet is retained).
 * All other negative results are errors. Decoder operations are serialized
 * internally; callers must finish in-flight operations before destroying one.
 */
HC_API const char *hc_name(void);
HC_API int hc_max_payload(int mode);
HC_API void *hc_create(int mode, int sample_rate);
HC_API void hc_destroy(void *handle);
HC_API int hc_encode(int mode, const uint8_t *data, int length, float *out, int capacity);
HC_API int hc_feed(void *handle, const float *mono, int count);
HC_API int hc_receive(void *handle, uint8_t *out, int capacity);
#ifdef __cplusplus
}
#endif
#endif
