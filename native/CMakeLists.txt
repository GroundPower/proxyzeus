cmake_minimum_required(VERSION 3.20)
project(zeus_native C)
set(CMAKE_C_STANDARD 99)
set(ENET_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/enet")
add_library(zeus_native SHARED bridge.c
  ${ENET_DIR}/callbacks.c ${ENET_DIR}/compress.c ${ENET_DIR}/host.c
  ${ENET_DIR}/list.c ${ENET_DIR}/packet.c ${ENET_DIR}/peer.c
  ${ENET_DIR}/protocol.c ${ENET_DIR}/win32.c)
target_include_directories(zeus_native PRIVATE ${ENET_DIR}/include)
target_compile_definitions(zeus_native PRIVATE _CRT_SECURE_NO_WARNINGS)
target_link_libraries(zeus_native PRIVATE ws2_32 winmm)
