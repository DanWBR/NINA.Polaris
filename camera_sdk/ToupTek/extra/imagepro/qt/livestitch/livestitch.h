#ifndef __MAIN_H__
#define __MAIN_H__

#include <QMainWindow>
#include <QPushButton>
#include <QComboBox>
#include <QLabel>
#include <QTimer>
#include <QCheckBox>
#include <QSlider>
#include <QString>
#include <QGroupBox>
#include <QBoxLayout>
#include <QVBoxLayout>
#include <QMenu>
#include <QMessageBox>
#include <toupcam.h>
#include <imagepro.h>
#include <qdebug.h>

class MainWindow : public QMainWindow
{
    Q_OBJECT
    ToupcamDeviceV2 m_cur;
    HToupcam        m_hcam;
    QComboBox*      m_cmb_res;
    QCheckBox*      m_cbox_auto;
    QCheckBox*      m_cbox_crop;
    QSlider*        m_slider_expoTime;
    QSlider*        m_slider_expoGain;
    QSlider*        m_slider_temp;
    QSlider*        m_slider_tint;
    QLabel*         m_lbl_expoTime;
    QLabel*         m_lbl_expoGain;
    QLabel*         m_lbl_temp;
    QLabel*         m_lbl_tint;
    QLabel*         m_lbl_video;
    QLabel*         m_lbl_video2;
    QLabel*         m_lbl_quality;
    QPushButton*    m_btn_autoWB;
    QPushButton*    m_btn_open;
    QPushButton*    m_btn_snap;
    QPushButton*    m_btn_stitch;
    unsigned        m_imgWidth;
    unsigned        m_imgHeight;
    uchar*          m_pData;
    int             m_res;
    int             m_temp;
    int             m_tint;
    unsigned        m_count;
    HImageproStitch m_handel;
    bool            m_bStitch;
    bool            m_bcrop;
public:
    MainWindow(QWidget* parent = nullptr);
protected:
    void closeEvent(QCloseEvent*) override;
signals:
    void evtCallback(unsigned nEvent);
    void imgSthCallback(eImageproStitchEvent nEvent);
    void imgCallback(int outW, int outH, eImageproStitchQuality quality);
private:
    void onBtnOpen();
    void onBtnSnap();
    void onBtnStitch();
    void handleImageEvent();
    void handleExpoEvent();
    void handleTempTintEvent();
    void handleStillImageEvent();
    void openCamera();
    void closeCamera();
    void startCamera();
    static void __stdcall eventCallBack(unsigned nEvent, void* pCallbackCtx);
    static void __stdcall imageCallBack(void* ctx, void* outData, int stride, int outW, int outH, int curW, int curH, int curType,
                                        int posX, int posY, eImageproStitchQuality quality, float sharpness, int bUpdate, int bSize);
    static void __stdcall imageSthCallBack(void* ctx, eImageproStitchEvent evt);
    static QVBoxLayout* makeLayout(QLabel*, QSlider*, QLabel*, QLabel*, QSlider*, QLabel*);
};
#endif
