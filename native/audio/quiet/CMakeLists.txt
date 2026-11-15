cmake_minimum_required(VERSION 3.15)
project(HorizonQuiet C)
set(CMAKE_C_STANDARD 99)
get_filename_component(HORIZON_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../.." ABSOLUTE)
set(QUIET_ROOT "${HORIZON_ROOT}/third_party/quiet/quiet")
set(DEPS_ROOT "${HORIZON_ROOT}/build/quiet")

add_library(horizon_quiet SHARED horizon_quiet.c
    "${QUIET_ROOT}/src/demodulator.c" "${QUIET_ROOT}/src/modulator.c"
    "${QUIET_ROOT}/src/utility.c" "${QUIET_ROOT}/src/decoder.c"
    "${QUIET_ROOT}/src/encoder.c" "${QUIET_ROOT}/src/profile.c"
    "${QUIET_ROOT}/src/error.c" "${QUIET_ROOT}/src/ring.c")
target_include_directories(horizon_quiet PRIVATE
    "${QUIET_ROOT}/include" "${DEPS_ROOT}/include" "${DEPS_ROOT}/jansson/include")
target_compile_definitions(horizon_quiet PRIVATE RING_BLOCKING=0 QUIET_PTHREAD_ERROR=0
    QUIET_PROFILES_LOCATION="quiet-profiles.json")
target_compile_options(horizon_quiet PRIVATE -Wall -Wextra -Wno-unused-parameter)
target_link_options(horizon_quiet PRIVATE -static -Wl,--exclude-all-symbols)
target_link_libraries(horizon_quiet PRIVATE "${DEPS_ROOT}/liquid-dsp/libliquid.a"
    "${DEPS_ROOT}/libfec/libfec.a" "${DEPS_ROOT}/jansson/lib/libjansson.a" m advapi32)
set_target_properties(horizon_quiet PROPERTIES PREFIX "" RUNTIME_OUTPUT_DIRECTORY "${HORIZON_ROOT}/native/audio/bin")

add_executable(quiet_roundtrip quiet_roundtrip.c)
target_compile_options(quiet_roundtrip PRIVATE -Wall -Wextra)
target_link_options(quiet_roundtrip PRIVATE -static)
target_link_libraries(quiet_roundtrip PRIVATE horizon_quiet)
set_target_properties(quiet_roundtrip PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${HORIZON_ROOT}/native/audio/bin")
