#include "sdr_api.h"
#ifdef _WIN32
#include <windows.h>
typedef HMODULE LibHandle;
#else
#include <dlfcn.h>
typedef void *LibHandle;
#endif

static LibHandle OpenLib(const std::vector<std::string> &dirs, const std::vector<std::string> &names, std::string &err)
{
    for (const auto &name : names)
    {
        std::vector<std::string> candidates;
        for (const auto &d : dirs)
            if (!d.empty()) candidates.push_back(d + "/" + name);
        candidates.push_back(name); // system search path
        for (const auto &path : candidates)
        {
#ifdef _WIN32
            // Let the DLL find its own dependencies (libusb, FTD3XX...) in the same folder.
            std::string dir = path.substr(0, path.find_last_of("/\\") == std::string::npos ? 0 : path.find_last_of("/\\"));
            if (!dir.empty()) SetDllDirectoryA(dir.c_str());
            LibHandle h = LoadLibraryA(path.c_str());
            SetDllDirectoryA(nullptr);
#else
            LibHandle h = dlopen(path.c_str(), RTLD_NOW);
#endif
            if (h) return h;
        }
    }
    err = "could not load " + names[0] +
#ifdef _WIN32
          " (install the SDR drivers, e.g. PothosSDR, or set the driver folder)"
#else
          " (install it: sudo apt install limesuite / hackrf / libiio0)"
#endif
          ;
    return nullptr;
}

template <typename T>
static bool Bind(LibHandle h, const char *sym, T &fn, std::string &err)
{
#ifdef _WIN32
    fn = reinterpret_cast<T>(GetProcAddress(h, sym));
#else
    fn = reinterpret_cast<T>(dlsym(h, sym));
#endif
    if (!fn) { err = std::string("missing function ") + sym; return false; }
    return true;
}

bool LimeApi::Load(const std::vector<std::string> &dirs, std::string &err)
{
    LibHandle h = OpenLib(dirs, {"LimeSuite.dll", "libLimeSuite.dll", "libLimeSuite.so", "libLimeSuite.so.23.11-1", "libLimeSuite.so.22.09-1", "libLimeSuite.so.20.10-1", "libLimeSuite.so.20.01-1"}, err);
    if (!h) return false;
    return Bind(h, "LMS_GetDeviceList", GetDeviceList, err) && Bind(h, "LMS_Open", Open, err) &&
           Bind(h, "LMS_Close", Close, err) && Bind(h, "LMS_Init", Init, err) &&
           Bind(h, "LMS_EnableChannel", EnableChannel, err) && Bind(h, "LMS_SetSampleRate", SetSampleRate, err) &&
           Bind(h, "LMS_SetLOFrequency", SetLOFrequency, err) && Bind(h, "LMS_SetAntenna", SetAntenna, err) &&
           Bind(h, "LMS_SetNormalizedGain", SetNormalizedGain, err) && Bind(h, "LMS_SetLPFBW", SetLPFBW, err) &&
           Bind(h, "LMS_Calibrate", Calibrate, err) && Bind(h, "LMS_SetupStream", SetupStream, err) &&
           Bind(h, "LMS_DestroyStream", DestroyStream, err) && Bind(h, "LMS_StartStream", StartStream, err) &&
           Bind(h, "LMS_StopStream", StopStream, err) && Bind(h, "LMS_GetStreamStatus", GetStreamStatus, err) &&
           Bind(h, "LMS_SendStream", SendStream, err) && Bind(h, "LMS_GetLastErrorMessage", GetLastErrorMessage, err);
}

bool HackrfApi::Load(const std::vector<std::string> &dirs, std::string &err)
{
    LibHandle h = OpenLib(dirs, {"hackrf.dll", "libhackrf.dll", "libhackrf.so", "libhackrf.so.0"}, err);
    if (!h) return false;
    return Bind(h, "hackrf_init", init, err) && Bind(h, "hackrf_exit", exit_, err) &&
           Bind(h, "hackrf_open", open, err) && Bind(h, "hackrf_close", close, err) &&
           Bind(h, "hackrf_start_tx", start_tx, err) && Bind(h, "hackrf_stop_tx", stop_tx, err) &&
           Bind(h, "hackrf_set_freq", set_freq, err) && Bind(h, "hackrf_set_sample_rate", set_sample_rate, err) &&
           Bind(h, "hackrf_set_baseband_filter_bandwidth", set_baseband_filter_bandwidth, err) &&
           Bind(h, "hackrf_compute_baseband_filter_bw", compute_baseband_filter_bw, err) &&
           Bind(h, "hackrf_set_txvga_gain", set_txvga_gain, err) && Bind(h, "hackrf_set_amp_enable", set_amp_enable, err) &&
           Bind(h, "hackrf_error_name", error_name, err);
}

bool IioApi::Load(const std::vector<std::string> &dirs, std::string &err)
{
    LibHandle h = OpenLib(dirs, {"libiio.dll", "libiio-0.dll", "iio.dll", "libiio.so.0", "libiio.so"}, err);
    if (!h)
    {
#ifdef _WIN32
        err += " - install ADI's PlutoSDR drivers / libiio";
#endif
        return false;
    }
    return Bind(h, "iio_create_context_from_uri", create_context_from_uri, err) &&
           Bind(h, "iio_context_destroy", context_destroy, err) &&
           Bind(h, "iio_context_find_device", context_find_device, err) &&
           Bind(h, "iio_device_find_channel", device_find_channel, err) &&
           Bind(h, "iio_channel_attr_write_longlong", channel_attr_write_longlong, err) &&
           Bind(h, "iio_channel_attr_write_double", channel_attr_write_double, err) &&
           Bind(h, "iio_channel_enable", channel_enable, err) &&
           Bind(h, "iio_device_create_buffer", device_create_buffer, err) &&
           Bind(h, "iio_buffer_destroy", buffer_destroy, err) &&
           Bind(h, "iio_buffer_push", buffer_push, err) &&
           Bind(h, "iio_buffer_start", buffer_start, err) &&
           Bind(h, "iio_buffer_end", buffer_end, err) &&
           Bind(h, "iio_buffer_step", buffer_step, err) &&
           Bind(h, "iio_device_set_kernel_buffers_count", device_set_kernel_buffers_count, err) &&
           Bind(h, "iio_create_scan_context", create_scan_context, err) &&
           Bind(h, "iio_scan_context_destroy", scan_context_destroy, err) &&
           Bind(h, "iio_scan_context_get_info_list", scan_context_get_info_list, err) &&
           Bind(h, "iio_context_info_list_free", context_info_list_free, err) &&
           Bind(h, "iio_context_info_get_uri", context_info_get_uri, err) &&
           Bind(h, "iio_context_info_get_description", context_info_get_description, err);
}
