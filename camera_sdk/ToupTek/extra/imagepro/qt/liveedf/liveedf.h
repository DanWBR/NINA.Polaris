#ifndef __liveedf_H__
#define __liveedf_H__

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

class MainWidget : public QWidget
{
    Q_OBJECT
    QCheckBox*      m_cbox_auto;
    QPushButton*    m_btn_open;
    QTimer*         m_timer;
    HToupcam        m_hcam;
    HImageproEdf    m_edf;
	QLabel*         m_lbl_edf;
    QLabel*         m_lbl_video;
    QLabel*         m_lbl_frame;
    int             m_imgWidth, m_imgHeight;
    uchar*          m_pVideoData;
    uchar*          m_pEdfData;
public:
    MainWidget(QWidget* parent = nullptr);
protected:
    void closeEvent(QCloseEvent*) override;
signals:
    void cameraCallback(unsigned nEvent);
    void edfCallback(eImageproEdfEvent evt);
private:
    void onBtnOpen();
    void handleImageEvent();
    void closeCamera();
    static void __stdcall CameraCallBack(unsigned nEvent, void* pCallbackCtx);
    static void __cdecl EdfCallback(void* ctx, int result, void* outData, int stride, int outW, int outH, int outType);
    static void __cdecl EdfECallback(void* ctx, eImageproEdfEvent evt);
};

#endif
