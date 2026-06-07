cmake_minimum_required (VERSION 3.12)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 11)

project (TestPlayerOneSDKDemo_C)

include_directories(${PROJECT_SOURCE_DIR}/../../include/)

if(WIN32)
    # won't work before project()!    
    if(CMAKE_SIZEOF_VOID_P EQUAL 8) # 64 bits
        link_directories(${PROJECT_SOURCE_DIR}/../../lib/x64)

    elseif(CMAKE_SIZEOF_VOID_P EQUAL 4) # 32 bits
        link_directories(${PROJECT_SOURCE_DIR}/../../lib/x86)

    endif()
else()
    link_directories(${PROJECT_SOURCE_DIR}/../../lib/)
endif()

link_libraries(PlayerOneCamera)

add_executable(TestPlayerOneSDKDemo_C main.c)

target_link_libraries(TestPlayerOneSDKDemo_C PlayerOneCamera)
