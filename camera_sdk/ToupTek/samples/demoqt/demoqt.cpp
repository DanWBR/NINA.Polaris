#include <QApplication>
#include "demoqt.h"

MainWidget::MainWidget(QWidget* parent)
    : QWidget(parent)
    , m_hcam(nullptr)
    , m_timer(new QTimer(this))
    , m_imgWidth(0), m_imgHeight(0), m_pData(nullptr)
    , m_res(0), m_temp(TOUPCAM_TEMP_DEF), m_tint(TOUPCAM_TINT_DEF), m_count(0)
{
    setMinimumSize(1024, 768);

    QGridLayout* gmain = new QGridLayout();

    QGroupBox* gboxres = new QGroupBox("Resolution");
    {
        m_cmb_res = new QComboBox();
        m_cmb_res->setEnabled(false);
        connect(m_cmb_res, QOverload<int>::of(&QComboBox::currentIndexChanged), this, [this](int index)
        {
            if (m_hcam) //step 1: stop camera
                Toupcam_Stop(m_hcam);

            m_res = index;
            m_imgWidth = m_cur.model->res[index].width;
            m_imgHeight = m_cur.model->res[index].height;

            if (m_hcam) //step 2: restart camera
            {
                Toupcam_put_eSize(m_hcam, static_cast<unsigned>(m_res));
                startCamera();
            }
        });

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_cmb_res);
        gboxres->setLayout(v);
    }

    QGroupBox* gboxexp = new QGroupBox("Exposure");
    {
        m_cbox_auto = new QCheckBox("Auto exposure");
        m_cbox_auto->setEnabled(false);
        m_lbl_expoTime = new QLabel("0");
        m_lbl_expoGain = new QLabel("0");
        m_slider_expoTime = new QSlider(Qt::Horizontal);
        m_slider_expoGain = new QSlider(Qt::Horizontal);
        m_slider_expoTime->setEnabled(false);
        m_slider_expoGain->setEnabled(false);
        connect(m_cbox_auto, &QCheckBox::stateChanged, this, [this](bool state)
        {
            if (m_hcam)
            {
                Toupcam_put_AutoExpoEnable(m_hcam, state ? 1 : 0);
                m_slider_expoTime->setEnabled(!state);
                m_slider_expoGain->setEnabled(!state);
            }
        });
        connect(m_slider_expoTime, &QSlider::valueChanged, this, [this](int value)
        {
            if (m_hcam)
            {
                m_lbl_expoTime->setText(QString::number(value));
                if (!m_cbox_auto->isChecked())
                   Toupcam_put_ExpoTime(m_hcam, value);
            }
        });
        connect(m_slider_expoGain, &QSlider::valueChanged, this, [this](int value)
        {
            if (m_hcam)
            {
                m_lbl_expoGain->setText(QString::number(value));
                if (!m_cbox_auto->isChecked())
                    Toupcam_put_ExpoAGain(m_hcam, value);
            }
        });

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_cbox_auto);
        v->addLayout(makeLayout(new QLabel("Time(us):"), m_slider_expoTime, m_lbl_expoTime, new QLabel("Gain(%):"), m_slider_expoGain, m_lbl_expoGain));
        gboxexp->setLayout(v);
    }

    QGroupBox* gboxwb = new QGroupBox("White balance");
    {
        m_btn_autoWB = new QPushButton("White balance");
        m_btn_autoWB->setEnabled(false);
        connect(m_btn_autoWB, &QPushButton::clicked, this, [this]()
        {
            Toupcam_AwbOnce(m_hcam, nullptr, nullptr);
        });
        m_lbl_temp = new QLabel(QString::number(TOUPCAM_TEMP_DEF));
        m_lbl_tint = new QLabel(QString::number(TOUPCAM_TINT_DEF));
        m_slider_temp = new QSlider(Qt::Horizontal);
        m_slider_tint = new QSlider(Qt::Horizontal);
        m_slider_temp->setRange(TOUPCAM_TEMP_MIN, TOUPCAM_TEMP_MAX);
        m_slider_temp->setValue(TOUPCAM_TEMP_DEF);
        m_slider_tint->setRange(TOUPCAM_TINT_MIN, TOUPCAM_TINT_MAX);
        m_slider_tint->setValue(TOUPCAM_TINT_DEF);
        m_slider_temp->setEnabled(false);
        m_slider_tint->setEnabled(false);
        connect(m_slider_temp, &QSlider::valueChanged, this, [this](int value)
        {
            m_temp = value;
            if (m_hcam)
                Toupcam_put_TempTint(m_hcam, m_temp, m_tint);
            m_lbl_temp->setText(QString::number(value));
        });
        connect(m_slider_tint, &QSlider::valueChanged, this, [this](int value)
        {
            m_tint = value;
            if (m_hcam)
                Toupcam_put_TempTint(m_hcam, m_temp, m_tint);
            m_lbl_tint->setText(QString::number(value));
        });

        QVBoxLayout* v = new QVBoxLayout();
        v->addLayout(makeLayout(new QLabel("Temperature:"), m_slider_temp, m_lbl_temp, new QLabel("Tint:"), m_slider_tint, m_lbl_tint));
        v->addWidget(m_btn_autoWB);
        gboxwb->setLayout(v);
    }

    {
        m_btn_open = new QPushButton("Open");
        connect(m_btn_open, &QPushButton::clicked, this, &MainWidget::onBtnOpen);
        m_btn_snap = new QPushButton("Snap");
        m_btn_snap->setEnabled(false);
        connect(m_btn_snap, &QPushButton::clicked, this, &MainWidget::onBtnSnap);

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(gboxres);
        v->addWidget(gboxexp);
        v->addWidget(gboxwb);
        v->addWidget(m_btn_open);
        v->addWidget(m_btn_snap);
        v->addStretch();
        gmain->addLayout(v, 0, 0);
    }

    {
        m_lbl_frame = new QLabel();
        m_lbl_video = new QLabel();

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_lbl_video, 1);
        v->addWidget(m_lbl_frame);
        gmain->addLayout(v, 0, 1);
    }

    gmain->setColumnStretch(0, 1);
    gmain->setColumnStretch(1, 4);
    setLayout(gmain);

    connect(this, &MainWidget::evtCallback, this, [this](unsigned nEvent)
    {
        /* this run in the UI thread */
        if (m_hcam)
        {
            if (TOUPCAM_EVENT_IMAGE == nEvent)
                handleImageEvent();
            else if (TOUPCAM_EVENT_EXPOSURE == nEvent)
                handleExpoEvent();
            else if (TOUPCAM_EVENT_TEMPTINT == nEvent)
                handleTempTintEvent();
            else if (TOUPCAM_EVENT_STILLIMAGE == nEvent)
                handleStillImageEvent();
            else if (TOUPCAM_EVENT_ERROR == nEvent)
            {
                closeCamera();
                QMessageBox::warning(this, "Warning", "Generic error.");
            }
            else if (TOUPCAM_EVENT_DISCONNECTED == nEvent)
            {
                closeCamera();
                QMessageBox::warning(this, "Warning", "Camera disconnect.");
            }
        }
    });

    connect(m_timer, &QTimer::timeout, this, [this]()
    {
        unsigned nFrame = 0, nTime = 0, nTotalFrame = 0;
        if (m_hcam && SUCCEEDED(Toupcam_get_FrameRate(m_hcam, &nFrame, &nTime, &nTotalFrame)) && (nTime > 0))
            m_lbl_frame->setText(QString::asprintf("%u, fps = %.1f", nTotalFrame, nFrame * 1000.0 / nTime));
    });
}

void MainWidget::closeCamera()
{
    if (m_hcam)
    {
        Toupcam_Close(m_hcam);
        m_hcam = nullptr;
    }
    delete[] m_pData;
    m_pData = nullptr;

    m_btn_open->setText("Open");
    m_timer->stop();
    m_lbl_frame->clear();
    m_cbox_auto->setEnabled(false);
    m_slider_expoGain->setEnabled(false);
    m_slider_expoTime->setEnabled(false);
    m_btn_autoWB->setEnabled(false);
    m_slider_temp->setEnabled(false);
    m_slider_tint->setEnabled(false);
    m_btn_snap->setEnabled(false);
    m_cmb_res->setEnabled(false);
    m_cmb_res->clear();
}

void MainWidget::closeEvent(QCloseEvent*)
{
    closeCamera();
}

void MainWidget::startCamera()
{
    if (m_pData)
    {
        delete[] m_pData;
        m_pData = nullptr;
    }
    m_pData = new uchar[TDIBWIDTHBYTES(m_imgWidth * 24) * m_imgHeight];
    unsigned uimax = 0, uimin = 0, uidef = 0;
    unsigned short usmax = 0, usmin = 0, usdef = 0;
    Toupcam_get_ExpTimeRange(m_hcam, &uimin, &uimax, &uidef);
    m_slider_expoTime->setRange(uimin, uimax);
    Toupcam_get_ExpoAGainRange(m_hcam, &usmin, &usmax, &usdef);
    m_slider_expoGain->setRange(usmin, usmax);
    if (0 == (m_cur.model->flag & TOUPCAM_FLAG_MONO))
        handleTempTintEvent();
    handleExpoEvent();
    if (SUCCEEDED(Toupcam_StartPullModeWithCallback(m_hcam, eventCallBack, this)))
    {
        m_cmb_res->setEnabled(true);
        m_cbox_auto->setEnabled(true);
        m_btn_autoWB->setEnabled(0 == (m_cur.model->flag & TOUPCAM_FLAG_MONO));
        m_slider_temp->setEnabled(0 == (m_cur.model->flag & TOUPCAM_FLAG_MONO));
        m_slider_tint->setEnabled(0 == (m_cur.model->flag & TOUPCAM_FLAG_MONO));
        m_btn_open->setText("Close");
        m_btn_snap->setEnabled(true);

        int bAuto = 0;
        Toupcam_get_AutoExpoEnable(m_hcam, &bAuto);
        m_cbox_auto->setChecked(1 == bAuto);
        
        m_timer->start(1000);
    }
    else
    {
        closeCamera();
        QMessageBox::warning(this, "Warning", "Failed to start camera.");
    }
}

void MainWidget::openCamera()
{
    m_hcam = Toupcam_Open(m_cur.id);
    if (m_hcam)
    {
        Toupcam_get_eSize(m_hcam, (unsigned*)&m_res);
        m_imgWidth = m_cur.model->res[m_res].width;
        m_imgHeight = m_cur.model->res[m_res].height;
        {
            const QSignalBlocker blocker(m_cmb_res);
            m_cmb_res->clear();
            for (unsigned i = 0; i < m_cur.model->preview; ++i)
                m_cmb_res->addItem(QString::asprintf("%u*%u", m_cur.model->res[i].width, m_cur.model->res[i].height));
            m_cmb_res->setCurrentIndex(m_res);
            m_cmb_res->setEnabled(true);
        }

        Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_BYTEORDER, 0); //Qimage use RGB byte order
        Toupcam_put_AutoExpoEnable(m_hcam, 1);
        startCamera();
    }
}

void MainWidget::onBtnOpen()
{
    if (m_hcam)
        closeCamera();
    else
    {
        ToupcamDeviceV2 arr[TOUPCAM_MAX] = { 0 };
        unsigned count = Toupcam_EnumV2(arr);
        if (0 == count)
            QMessageBox::warning(this, "Warning", "No camera found.");
        else if (1 == count)
        {
            m_cur = arr[0];
            openCamera();
        }
        else
        {
            QMenu menu;
            for (unsigned i = 0; i < count; ++i)
            {
                menu.addAction(
#if defined(_WIN32)
                            QString::fromWCharArray(arr[i].displayname)
#else
                            arr[i].displayname
#endif
                            , this, [this, i, arr](bool)
                {
                    m_cur = arr[i];
                    openCamera();
                });
            }
            menu.exec(mapToGlobal(m_btn_snap->pos()));
        }
    }
}

void MainWidget::onBtnSnap()
{
    if (m_hcam)
    {
        if (0 == m_cur.model->still)    // not support still image capture
        {
            if (m_pData)
            {
                QImage image(m_pData, m_imgWidth, m_imgHeight, QImage::Format_RGB888);
                image.save(QString::asprintf("demoqt_%u.jpg", ++m_count));
            }
        }
        else
        {
            QMenu menu;
            for (unsigned i = 0; i < m_cur.model->still; ++i)
            {
                menu.addAction(QString::asprintf("%u*%u", m_cur.model->res[i].width, m_cur.model->res[i].height), this, [this, i](bool)
                {
                    Toupcam_Snap(m_hcam, i);
                });
            }
            menu.exec(mapToGlobal(m_btn_snap->pos()));
        }
    }
}

void MainWidget::eventCallBack(unsigned nEvent, void* pCallbackCtx)
{
    MainWidget* pThis = reinterpret_cast<MainWidget*>(pCallbackCtx);
    emit pThis->evtCallback(nEvent);
}

void MainWidget::handleImageEvent()
{
    unsigned width = 0, height = 0;
    if (SUCCEEDED(Toupcam_PullImage(m_hcam, m_pData, 24, &width, &height)))
    {
        QImage image(m_pData, width, height, QImage::Format_RGB888);
        QImage newimage = image.scaled(m_lbl_video->width(), m_lbl_video->height(), Qt::KeepAspectRatio, Qt::FastTransformation);
        m_lbl_video->setPixmap(QPixmap::fromImage(newimage));
    }
}

void MainWidget::handleExpoEvent()
{
    unsigned time = 0;
    unsigned short gain = 0;
    Toupcam_get_ExpoTime(m_hcam, &time);
    Toupcam_get_ExpoAGain(m_hcam, &gain);
    {
        const QSignalBlocker blocker(m_slider_expoTime);
        m_slider_expoTime->setValue(int(time));
    }
    {
        const QSignalBlocker blocker(m_slider_expoGain);
        m_slider_expoGain->setValue(int(gain));
    }
    m_lbl_expoTime->setText(QString::number(time));
    m_lbl_expoGain->setText(QString::number(gain));
}

void MainWidget::handleTempTintEvent()
{
    int nTemp = 0, nTint = 0;
    if (SUCCEEDED(Toupcam_get_TempTint(m_hcam, &nTemp, &nTint)))
    {
        {
            const QSignalBlocker blocker(m_slider_temp);
            m_slider_temp->setValue(nTemp);
        }
        {
            const QSignalBlocker blocker(m_slider_tint);
            m_slider_tint->setValue(nTint);
        }
        m_lbl_temp->setText(QString::number(nTemp));
        m_lbl_tint->setText(QString::number(nTint));
    }
}

void MainWidget::handleStillImageEvent()
{
    unsigned width = 0, height = 0;
    if (SUCCEEDED(Toupcam_PullStillImage(m_hcam, nullptr, 24, &width, &height))) // peek
    {
        std::vector<uchar> vec(TDIBWIDTHBYTES(width * 24) * height);
        if (SUCCEEDED(Toupcam_PullStillImage(m_hcam, &vec[0], 24, &width, &height)))
        {
            QImage image(&vec[0], width, height, QImage::Format_RGB888);
            image.save(QString::asprintf("demoqt_%u.jpg", ++m_count));
        }
    }
}

QVBoxLayout* MainWidget::makeLayout(QLabel* lbl1, QSlider* sli1, QLabel* val1, QLabel* lbl2, QSlider* sli2, QLabel* val2)
{
    QHBoxLayout* hlyt1 = new QHBoxLayout();
    hlyt1->addWidget(lbl1);
    hlyt1->addStretch();
    hlyt1->addWidget(val1);
    QHBoxLayout* hlyt2 = new QHBoxLayout();
    hlyt2->addWidget(lbl2);
    hlyt2->addStretch();
    hlyt2->addWidget(val2);
    QVBoxLayout* vlyt = new QVBoxLayout();
    vlyt->addLayout(hlyt1);
    vlyt->addWidget(sli1);
    vlyt->addLayout(hlyt2);
    vlyt->addWidget(sli2);
    return vlyt;
}

int main(int argc, char* argv[])
{
    Toupcam_GigeEnable(nullptr, nullptr);
    QApplication a(argc, argv);
    MainWidget mw;
    mw.show();
    return a.exec();
}
