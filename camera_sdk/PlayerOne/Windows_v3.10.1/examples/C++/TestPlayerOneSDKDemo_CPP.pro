TEMPLATE = app
CONFIG += console c++11
CONFIG -= app_bundle
CONFIG -= qt

SOURCES += \
        POACamera.cpp \
        main.cpp

HEADERS += \
    POACamera.h

win32: {
    contains(QT_ARCH, i386) {
        LIBS += -L$$PWD/../../lib/x86/ -lPlayerOneCamera
    } else {
        LIBS += -L$$PWD/../../lib/x64/ -lPlayerOneCamera
    }

}
else:unix: LIBS += -L$$PWD/../../lib/ -lPlayerOneCamera

INCLUDEPATH += $$PWD/../../include
DEPENDPATH += $$PWD/../../include

