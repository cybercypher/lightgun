cmake_minimum_required(VERSION 3.13)

# Point this at a Pico SDK 2.x checkout, e.g.:
#   export PICO_SDK_PATH=$HOME/pico-sdk
set(PICO_SDK_PATH $ENV{PICO_SDK_PATH} CACHE PATH "Path to the Raspberry Pi Pico SDK")
if (NOT PICO_SDK_PATH OR NOT EXISTS "${PICO_SDK_PATH}/external/pico_sdk_import.cmake")
    message(FATAL_ERROR
        "Set PICO_SDK_PATH to a Pico SDK 2.x checkout (or pass "
        "-DPICO_SDK_PATH=/path/to/pico-sdk).")
endif()

include(${PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(gun_bridge C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

pico_sdk_init()

add_executable(gun_bridge
    main.c
    usb_descriptors.c
)

target_include_directories(gun_bridge PRIVATE ${CMAKE_CURRENT_LIST_DIR})

target_link_libraries(gun_bridge
    pico_stdlib
    tinyusb_device
)

# The CDC interface belongs to the bridge protocol, not to stdio.
pico_enable_stdio_usb(gun_bridge 0)
pico_enable_stdio_uart(gun_bridge 0)

pico_add_extra_outputs(gun_bridge)
