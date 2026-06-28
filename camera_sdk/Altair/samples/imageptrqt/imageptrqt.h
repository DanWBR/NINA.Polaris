#ifndef __imageptrqt_H__
#define __imageptrqt_H__

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
#include <altaircam.h>

class MainWidget : public QWidget
{
    Q_OBJECT
    AltaircamDeviceV2 m_cur;
    HAltaircam        m_hcam;
    QComboBox*      m_cmb_res;
    QCheckBox*      m_cbox_auto;
    QSlider*        m_slider_expoTime;
    QSlider*        m_slider_expoGain;
    QSlider*        m_slider_temp;
    QSlider*        m_slider_tint;
    QLabel*         m_lbl_expoTime;
    QLabel*         m_lbl_expoGain;
    QLabel*         m_lbl_temp;
    QLabel*         m_lbl_tint;
    QLabel*         m_lbl_video;
    QLabel*         m_lbl_frame;
    QPushButton*    m_btn_autoWB;
    QPushButton*    m_btn_open;
    QPushButton*    m_btn_snap;
    QTimer*         m_timer;
    unsigned        m_imgWidth;
    unsigned        m_imgHeight;
    AltaircamImagePtr m_ptr;
    int             m_res;
    int             m_temp;
    int             m_tint;
    unsigned        m_count;
public:
    MainWidget(QWidget* parent = nullptr);
protected:
    void closeEvent(QCloseEvent*) override;
signals:
    void evtCallback(unsigned nEvent);
private:
    void onBtnOpen();
    void onBtnSnap();
    void handleImageEvent();
    void handleExpoEvent();
    void handleTempTintEvent();
    void handleStillImageEvent();
    void openCamera();
    void closeCamera();
    void startCamera();
    static void __stdcall eventCallBack(unsigned nEvent, void* pCallbackCtx);
    static QVBoxLayout* makeLayout(QLabel*, QSlider*, QLabel*, QLabel*, QSlider*, QLabel*);
};

#endif
