// Minimal runtime-loaded bindings for LimeSuite (LMS API) and libhackrf.
// Types and prototypes copied from LimeSuite.h (myriadrf/LimeSuite) and hackrf.h
// (greatscottgadgets/hackrf) so no SDK is needed at build time. The DLLs are
// loaded at run time from the user's driver install (e.g. PothosSDR).
#pragma once
#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

#ifdef _WIN32
#define SDR_CALL __cdecl
#else
#define SDR_CALL
#endif

// ---------------------------------------------------------------- LimeSuite
typedef double lms_float_type;
typedef void lms_device_t;
typedef char lms_info_str_t[256];

typedef struct
{
    uint64_t timestamp;
    bool waitForTimestamp;
    bool flushPartialPacket;
} lms_stream_meta_t;

typedef struct
{
    size_t handle;
    bool isTx;
    uint32_t channel;
    uint32_t fifoSize;
    float throughputVsLatency;
    enum { LMS_FMT_F32 = 0, LMS_FMT_I16, LMS_FMT_I12 } dataFmt;
    enum { LMS_LINK_FMT_DEFAULT = 0, LMS_LINK_FMT_I16, LMS_LINK_FMT_I12 } linkFmt;
} lms_stream_t;

typedef struct
{
    bool active;
    uint32_t fifoFilledCount;
    uint32_t fifoSize;
    uint32_t underrun;
    uint32_t overrun;
    uint32_t droppedPackets;
    lms_float_type sampleRate;
    lms_float_type linkRate;
    uint64_t timestamp;
} lms_stream_status_t;

struct LimeApi
{
    int (SDR_CALL *GetDeviceList)(lms_info_str_t *dev_list) = nullptr;
    int (SDR_CALL *Open)(lms_device_t **device, const lms_info_str_t info, void *args) = nullptr;
    int (SDR_CALL *Close)(lms_device_t *device) = nullptr;
    int (SDR_CALL *Init)(lms_device_t *device) = nullptr;
    int (SDR_CALL *EnableChannel)(lms_device_t *device, bool dir_tx, size_t chan, bool enabled) = nullptr;
    int (SDR_CALL *SetSampleRate)(lms_device_t *device, lms_float_type rate, size_t oversample) = nullptr;
    int (SDR_CALL *SetLOFrequency)(lms_device_t *device, bool dir_tx, size_t chan, lms_float_type frequency) = nullptr;
    int (SDR_CALL *SetAntenna)(lms_device_t *dev, bool dir_tx, size_t chan, size_t index) = nullptr;
    int (SDR_CALL *SetNormalizedGain)(lms_device_t *device, bool dir_tx, size_t chan, lms_float_type gain) = nullptr;
    int (SDR_CALL *SetLPFBW)(lms_device_t *device, bool dir_tx, size_t chan, lms_float_type bandwidth) = nullptr;
    int (SDR_CALL *Calibrate)(lms_device_t *device, bool dir_tx, size_t chan, double bw, unsigned flags) = nullptr;
    int (SDR_CALL *SetupStream)(lms_device_t *device, lms_stream_t *stream) = nullptr;
    int (SDR_CALL *DestroyStream)(lms_device_t *dev, lms_stream_t *stream) = nullptr;
    int (SDR_CALL *StartStream)(lms_stream_t *stream) = nullptr;
    int (SDR_CALL *StopStream)(lms_stream_t *stream) = nullptr;
    int (SDR_CALL *GetStreamStatus)(lms_stream_t *stream, lms_stream_status_t *status) = nullptr;
    int (SDR_CALL *SendStream)(lms_stream_t *stream, const void *samples, size_t sample_count, const lms_stream_meta_t *meta, unsigned timeout_ms) = nullptr;
    const char *(SDR_CALL *GetLastErrorMessage)(void) = nullptr;

    bool Load(const std::vector<std::string> &dirs, std::string &err);
};

// ---------------------------------------------------------------- libhackrf
typedef struct hackrf_device hackrf_device;

typedef struct
{
    hackrf_device *device;
    uint8_t *buffer;
    int buffer_length;
    int valid_length;
    void *rx_ctx;
    void *tx_ctx;
} hackrf_transfer;

typedef int (*hackrf_sample_block_cb_fn)(hackrf_transfer *transfer);

struct HackrfApi
{
    int (SDR_CALL *init)() = nullptr;
    int (SDR_CALL *exit_)() = nullptr;
    int (SDR_CALL *open)(hackrf_device **device) = nullptr;
    int (SDR_CALL *close)(hackrf_device *device) = nullptr;
    int (SDR_CALL *start_tx)(hackrf_device *device, hackrf_sample_block_cb_fn callback, void *tx_ctx) = nullptr;
    int (SDR_CALL *stop_tx)(hackrf_device *device) = nullptr;
    int (SDR_CALL *set_freq)(hackrf_device *device, const uint64_t freq_hz) = nullptr;
    int (SDR_CALL *set_sample_rate)(hackrf_device *device, const double freq_hz) = nullptr;
    int (SDR_CALL *set_baseband_filter_bandwidth)(hackrf_device *device, const uint32_t bandwidth_hz) = nullptr;
    uint32_t (SDR_CALL *compute_baseband_filter_bw)(const uint32_t bandwidth_hz) = nullptr;
    int (SDR_CALL *set_txvga_gain)(hackrf_device *device, uint32_t value) = nullptr;
    int (SDR_CALL *set_amp_enable)(hackrf_device *device, const uint8_t value) = nullptr;
    const char *(SDR_CALL *error_name)(int errcode) = nullptr;

    bool Load(const std::vector<std::string> &dirs, std::string &err);
};
