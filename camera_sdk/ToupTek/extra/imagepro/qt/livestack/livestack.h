#ifndef __livestack_H__
#define __livestack_H__

#include <QPushButton>
#include <QComboBox>
#include <QLabel>
#include <QTimer>
#include <QCheckBox>
#include <QString>
#include <QGridLayout>
#include <QMessageBox>
#include <toupcam.h>
#include <imagepro.h>
#include <imagepro_toupcam.h>
#include <mutex>

class MainWidget : public QWidget
{
    Q_OBJECT
    QCheckBox*      m_cbox_auto;
    QPushButton*    m_btn_open;
    QTimer*         m_timer;
    HToupcam        m_hcam;
    HLivestack      m_stack;
    QLabel*         m_lbl_stack;
    QLabel*         m_lbl_video;
    QLabel*         m_lbl_frame;
    int             m_imgWidth, m_imgHeight;
    uchar*          m_pVideoData;
    uchar*          m_pStackData;
    std::mutex      m_mtx;
public:
    MainWidget(QWidget* parent = nullptr);
protected:
    void closeEvent(QCloseEvent*) override;
signals:
    void cameraCallback(unsigned nEvent);
    void stackCallback(unsigned err);
private:
    void onBtnOpen();
    void handleImageEvent();
    void closeCamera();
    void TStackCallback(int width, int height, int type, eImageproLivestackError err, void* data);
    static void __stdcall CameraCallBack(unsigned nEvent, void* pCallbackCtx);
    static void __cdecl StackCallback(void* ctx, int width, int height, int type, eImageproLivestackError err, void* data);
};

#endif
