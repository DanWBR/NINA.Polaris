Prerequisites:
    (Please add the corresponding files based on the OpenCV version you are using and modify the demoopencv4.vcxproj project file. The default version used in the demo is 4.11.0)
    Add OpenCV 4.11.0 opencv2 header files and toupcam.h to the inc directory
    Add toupcam.lib to the lib directory
    Add toupcam.dll to the same directory as the executable file
x64 compilation conditions:
    DEBUG:
        Add opencv_world4110d.dll to the same directory as the executable file
        Add opencv_world4110d.lib to the lib directory
    RELEASE:
        Add opencv_world4110.dll to the same directory as the executable file
        Add opencv_world4110.lib to the lib directory