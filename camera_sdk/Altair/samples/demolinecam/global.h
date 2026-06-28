#ifndef GLOBAL_H
#define GLOBAL_H

#if defined(_WIN32)
#include <windows.h>
#endif
#include <QPushButton>
#include <QComboBox>
#include <QLabel>
#include <QTimer>
#include <QCheckBox>
#include <QSlider>
#include <QString>
#include <QGroupBox>
#include <QGridLayout>
#include <QVBoxLayout>
#include <QMenu>
#include <QMessageBox>
#include <QLineEdit>
#include <altaircam.h>

#if !defined(SAFE_FREE)
#define SAFE_FREE(p)	\
do { \
    if (p)	\
    {	\
            free(p);	\
            p = nullptr;	\
    }	\
} while (0);
#endif

#if !defined(CLAMP)
#define CLAMP(x, mn, mx)	\
do {						\
    if ((x) < (mn))			\
        (x) = (mn);			\
    else if ((x) > (mx))	\
        (x) = (mx);			\
} while (0)
#endif

int DpiScale(int x);
int DpiUnscale(int x);

extern int g_nDpi;

#endif // GLOBAL_H
