QT += core gui widgets
SOURCES += livestitch.cpp
HEADERS += livestitch.h
LIBS += -L$$PWD/./ -ltoupcam -limagepro
#CONFIG += console
