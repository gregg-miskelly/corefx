cmake_minimum_required(VERSION 2.8.12)
project(CoreFX)

set(CMAKE_MACOSX_RPATH ON)
set(CMAKE_INSTALL_PREFIX $ENV{__CMakeBinDir})
set(CMAKE_INCLUDE_CURRENT_DIR ON)
set(CMAKE_C_FLAGS "-std=c11")
set(CMAKE_CXX_FLAGS "-std=c++11")
set(CMAKE_SHARED_LIBRARY_PREFIX "")

add_compile_options(-Weverything)
add_compile_options(-Wno-format-nonliteral)
add_compile_options(-Wno-missing-prototypes)
add_compile_options(-Wno-disabled-macro-expansion)
add_compile_options(-Wno-c++98-compat)
add_compile_options(-Wno-c++98-compat-pedantic)
add_compile_options(-Werror)
add_compile_options(-fPIC)
add_compile_options(-I${CMAKE_CURRENT_SOURCE_DIR}/Common)
add_compile_options(-I${CMAKE_CURRENT_BINARY_DIR}/Common)
add_compile_options(-Wno-c99-extensions)

if (CMAKE_SYSTEM_PROCESSOR STREQUAL x86_64 OR CMAKE_SYSTEM_PROCESSOR STREQUAL amd64)
    add_definitions(-DBIT64=1)
elseif (CMAKE_SYSTEM_PROCESSOR STREQUAL armv7l)
    add_definitions(-DBIT32=1)
    # Because we don't use CMAKE_C_COMPILER/CMAKE_CXX_COMPILER to use clang
    # we have to set the triple by adding a compiler argument
    add_compile_options(-target armv7-linux-gnueabihf)
    add_compile_options(-mthumb)
    add_compile_options(-mfpu=vfpv3)
endif ()

string(TOUPPER ${CMAKE_BUILD_TYPE} UPPERCASE_CMAKE_BUILD_TYPE)
if (UPPERCASE_CMAKE_BUILD_TYPE STREQUAL DEBUG)
    add_compile_options(-g -O0)
    add_definitions(-DDEBUG)
elseif (UPPERCASE_CMAKE_BUILD_TYPE STREQUAL RELEASE)
    add_compile_options (-O3)
    add_definitions(-DNDEBUG)
else ()
    message(FATAL_ERROR "Unknown build type. Set CMAKE_BUILD_TYPE to DEBUG or RELEASE.")
endif ()

if (APPLE)
    add_definitions(-D__APPLE_USE_RFC_3542)
endif ()

include(configure.cmake)

add_subdirectory(System.Native)
add_subdirectory(System.Net.Http.Native)
add_subdirectory(System.Security.Cryptography.Native)
