cmake_minimum_required(VERSION 3.16)
project(datvtx_mod CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# libdvbmod (F5OEO) is fetched at build time into ${LIBDVBMOD_DIR}
set(LIBDVBMOD_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/libdvbmod/libdvbmod" CACHE PATH "libdvbmod source folder")
file(GLOB DVBMOD_SRC
    "${LIBDVBMOD_DIR}/libdvbmod.cpp"
    "${LIBDVBMOD_DIR}/DVB-S/*.cpp"
    "${LIBDVBMOD_DIR}/DVB-S2/*.cpp")
list(FILTER DVBMOD_SRC EXCLUDE REGEX "dvbs_receive\\.cpp$")
if(NOT DVBMOD_SRC)
    message(FATAL_ERROR "libdvbmod sources not found in ${LIBDVBMOD_DIR}")
endif()

add_library(dvbmod STATIC ${DVBMOD_SRC})
target_include_directories(dvbmod PUBLIC "${LIBDVBMOD_DIR}")
if(MSVC)
    target_compile_options(dvbmod PRIVATE /W0 /permissive /O2)
    target_compile_definitions(dvbmod PRIVATE _CRT_SECURE_NO_WARNINGS _USE_MATH_DEFINES)
else()
    target_compile_options(dvbmod PRIVATE -w -O2 -fpermissive)
endif()

add_executable(datvtx_mod src/main.cpp src/core.cpp src/radio.cpp src/sdr_api.cpp)
target_link_libraries(datvtx_mod PRIVATE dvbmod)
if(WIN32)
    target_link_libraries(datvtx_mod PRIVATE ws2_32)
    target_compile_definitions(datvtx_mod PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0601)
    if(MINGW)
        target_link_options(datvtx_mod PRIVATE -static -static-libgcc -static-libstdc++)
    endif()
    if(MSVC)
        set_property(TARGET datvtx_mod PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
        set_property(TARGET dvbmod PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
    endif()
else()
    find_package(Threads REQUIRED)
    target_link_libraries(datvtx_mod PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
endif()
