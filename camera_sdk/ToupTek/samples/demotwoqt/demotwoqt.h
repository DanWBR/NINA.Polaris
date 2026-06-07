#ifndef __demotwoqt_H__
#define __demotwoqt_H__

#include <QPushButton>
#include <QLabel>
#include <QTimer>
#include <QCheckBox>
#include <QSlider>
#include <QString>
#include <QGridLayout>
#include <QMessageBox>
#include <toupcam.h>

#if defined(_WIN32)
typedef wchar_t tchar;
#else
typedef char    tchar;
#endif

class MainWidget : public QWidget
{
    Q_OBJECT
    QCheckBox*      m_cbox_auto;
    QPushButton*    m_btn_open;
    QTimer*         m_timer;
    HToupcam        m_hcam[2];
    QLabel*         m_lbl_video[2];
    QLabel*         m_lbl_frame[2];
    int             m_imgWidth[2], m_imgHeight[2];
    uchar*          m_pData[2];
    struct callBackCtx {
        MainWidget* pthis;
        int idx;
    }               m_ctx[2];
public:
    MainWidget(QWidget* parent = nullptr);
protected:
    void closeEvent(QCloseEvent*) override;
signals:
    void evtCallback(int idx, unsigned nEvent);
private:
    void onBtnOpen();
    void handleImageEvent(int idx);
    void openCamera(int idx, const tchar* camId);
    void closeCamera(int idx);
    static void __stdcall eventCallBack(unsigned nEvent, void* pCallbackCtx);
};

#endif
