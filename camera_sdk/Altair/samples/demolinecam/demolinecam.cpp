#include <QApplication>
#include "demolinecam.h"

MainWidget::MainWidget(QWidget* parent)
    : QWidget(parent)
    , m_LineDataPanel(new LineDataPanel(this))
    , m_timer(new QTimer(this))
    , m_hcam(nullptr)
    , m_imgWidth(0)
    , m_imgHeight(0)
    , m_pData(nullptr)
    , m_pshowData(nullptr)
    , m_res(0)
    , m_temp(ALTAIRCAM_TEMP_DEF)
    , m_tint(ALTAIRCAM_TINT_DEF)
    , m_count(0)
    , m_bshow(false)
{
    setMinimumSize(1440, 512);
    showMaximized();
    QGridLayout* gmain = new QGridLayout();

    QGroupBox* gboxres = new QGroupBox("Resolution");
    {
        m_cmb_res = new QComboBox();
        m_cmb_res->setEnabled(false);
        connect(m_cmb_res, QOverload<int>::of(&QComboBox::currentIndexChanged), this, [this](int index)
        {
            if (m_hcam) //step 1: stop camera
                Altaircam_Stop(m_hcam);

            m_res = index;
            m_imgWidth = m_cur.model->res[index].width;
            m_imgHeight = m_cur.model->res[index].height;

            if (m_hcam) //step 2: restart camera
            {
                Altaircam_put_eSize(m_hcam, static_cast<unsigned>(m_res));
                startCamera();
            }
        });

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_cmb_res);
        gboxres->setLayout(v);
    }

    QGroupBox* gboxbitdepth = new QGroupBox("Bit depth");
    {
        m_rdo_lowBit = new QRadioButton("8");
        connect(m_rdo_lowBit, &QRadioButton::clicked, this, &MainWidget::onRdoLowBit);
        m_rdo_highBit = new QRadioButton("16");
        connect(m_rdo_highBit, &QRadioButton::clicked, this, &MainWidget::onRdoHighBit);
        QHBoxLayout* h = new QHBoxLayout();
        h->addWidget(m_rdo_lowBit);
        h->addWidget(m_rdo_highBit);
        gboxbitdepth->setLayout(h);
    }

    QGroupBox* gboxexp = new QGroupBox("Exposure");
    {
        m_sld_expoTime = new QSlider(Qt::Horizontal);
        m_sld_expoTime->setEnabled(false);
        connect(m_sld_expoTime, &QSlider::valueChanged, this, [this](int value)
        {
            if (m_hcam)
            {
                m_edt_expoTime->setText(QString::number(value));
                Altaircam_put_ExpoTime(m_hcam, value * 1000);
            }
        });

        m_btn_expoTime = new QPushButton("Set Expotime");
        m_btn_expoTime->setEnabled(false);
        connect(m_btn_expoTime, &QPushButton::clicked, this, [this]()
        {
            if (m_hcam)
            {
                int nexpo = m_edt_expoTime->text().toInt();
                Altaircam_put_ExpoTime(m_hcam, nexpo * 1000);
                {
                    const QSignalBlocker blocker(m_sld_expoTime);
                    m_sld_expoTime->setValue(nexpo);
                }
            }
        });

        m_edt_expoTime = new QLineEdit();
        m_edt_expoTime->setEnabled(false);

        QHBoxLayout* h = new QHBoxLayout();
        h->addWidget(m_edt_expoTime);
        h->addWidget(new QLabel("ms"));
        h->addWidget(m_btn_expoTime);
        QVBoxLayout* v = new QVBoxLayout();
        v->addLayout(h);
        v->addWidget(m_sld_expoTime);
        gboxexp->setLayout(v);
    }

    QGroupBox* gboxshowmode = new QGroupBox("Show mode");
    {
        m_rdo_poly = new QRadioButton("Polyline");
        connect(m_rdo_poly, &QRadioButton::clicked, this, &MainWidget::onRdoPoly);
        m_rdo_image = new QRadioButton("image");
        connect(m_rdo_image, &QRadioButton::clicked, this, &MainWidget::onRdoImage);
        QHBoxLayout* h = new QHBoxLayout();
        h->addWidget(m_rdo_poly);
        h->addWidget(m_rdo_image);
        gboxshowmode->setLayout(h);
        m_rdo_poly->setChecked(true);
    }

    QGroupBox* gboxTriggermode = new QGroupBox("Trigger Mode");
    {
        m_rdo_trigger = new QRadioButton("Trigger");
        connect(m_rdo_trigger, &QRadioButton::clicked, this, &MainWidget::onRdoTrigger);
        m_rdo_trigger->setEnabled(false);

        m_rdo_video = new QRadioButton("Video");
        connect(m_rdo_video, &QRadioButton::clicked, this, &MainWidget::onRdoVideo);
        m_rdo_video->setEnabled(false);

        m_btn_trigger = new QPushButton("Soft Trigger");
        connect(m_btn_trigger, &QPushButton::clicked, this, &MainWidget::onBtnTrigger);
        m_btn_trigger->setEnabled(false);

        m_cmb_triggerSource = new QComboBox();
        m_cmb_triggerSource->setEnabled(false);
        m_cmb_triggerSource->addItem("Opto-isolated input");
        m_cmb_triggerSource->addItem("GPIO0");
        m_cmb_triggerSource->addItem("GPIO1");
        m_cmb_triggerSource->addItem("Counter");
        m_cmb_triggerSource->addItem("PWM");
        m_cmb_triggerSource->addItem("Software");
        m_cmb_triggerSource->setCurrentIndex(0);
        connect(m_cmb_triggerSource, QOverload<int>::of(&QComboBox::currentIndexChanged), this, [this](int index)
        {
            Altaircam_IoControl(m_hcam, 0, ALTAIRCAM_IOCONTROLTYPE_SET_TRIGGERSOURCE, index, NULL);
            m_btn_trigger->setEnabled(index == 5 ? true : false);
        });

        QHBoxLayout* h = new QHBoxLayout();
        h->addWidget(m_rdo_video);
        h->addWidget(m_rdo_trigger);
        h->addWidget(m_cmb_triggerSource);
        h->addWidget(m_btn_trigger);
        gboxTriggermode->setLayout(h);
        m_rdo_video->setChecked(true);
    }

    QGroupBox* gboxframerate = new QGroupBox("Frame Rate");
    {
        m_lbl_frameRate = new QLabel();
        m_sld_frameRate = new QSlider(Qt::Horizontal);
        m_sld_frameRate->setEnabled(false);
        connect(m_sld_frameRate, &QSlider::valueChanged, this, [this](int val)
        {
            if (m_hcam)
            {
                m_lbl_frameRate->setText(QString::asprintf("%.1lf", val / 10.0));
                Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_PRECISE_FRAMERATE, val);
            }
        });

        QHBoxLayout* hlyt = new QHBoxLayout();
        hlyt->addWidget(new QLabel("FrameRate:"));
        hlyt->addStretch();
        hlyt->addWidget(m_lbl_frameRate);
        QVBoxLayout* vlyt = new QVBoxLayout();
        vlyt->addLayout(hlyt);
        vlyt->addWidget(m_sld_frameRate);

        gboxframerate->setLayout(vlyt);
    }

    QGroupBox* gboxTEC = new QGroupBox("TEC");
    {
        m_rdo_OpenTEC = new QRadioButton("Open");
        connect(m_rdo_OpenTEC, &QRadioButton::clicked, this, &MainWidget::onRdoOpenTEC);
        m_rdo_OpenTEC->setEnabled(false);

        m_rdo_CloseTEC = new QRadioButton("Close");
        connect(m_rdo_CloseTEC, &QRadioButton::clicked, this, &MainWidget::onRdoCloseTEC);
        m_rdo_CloseTEC->setEnabled(false);

        m_edt_TECtarget = new QLineEdit();
        m_edt_TECtarget->setEnabled(false);

        m_btn_setTEC = new QPushButton("Set");
        connect(m_btn_setTEC, &QPushButton::clicked, this, &MainWidget::onBtnSetTEC);
        m_btn_setTEC->setEnabled(false);
        m_lbl_Temperature = new QLabel("0");

        QHBoxLayout* h1 = new QHBoxLayout();
        h1->addWidget(new QLabel("TEC target:"));
        h1->addWidget(m_edt_TECtarget);
        h1->addWidget(m_btn_setTEC);

        QHBoxLayout* h2 = new QHBoxLayout();
        h2->addWidget(new QLabel("Temperature:"));
        h2->addWidget(m_lbl_Temperature);

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_rdo_OpenTEC);
        v->addLayout(h1);
        v->addWidget(m_rdo_CloseTEC);
        v->addLayout(h2);
        gboxTEC->setLayout(v);
    }

    {
        m_btn_open = new QPushButton("Open");
        connect(m_btn_open, &QPushButton::clicked, this, &MainWidget::onBtnOpen);

        m_btn_snap = new QPushButton("Snap");
        m_btn_snap->setEnabled(false);
        connect(m_btn_snap, &QPushButton::clicked, this, &MainWidget::onBtnSnap);

        m_lbl_frame = new QLabel();

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_btn_open);
        v->addWidget(m_btn_snap);
        v->addWidget(gboxres);
        v->addWidget(gboxbitdepth);
        v->addWidget(gboxTriggermode);
        v->addWidget(gboxexp);
        v->addWidget(gboxframerate);
        v->addWidget(gboxTEC);
        v->addWidget(gboxshowmode);
        v->addWidget(m_lbl_frame);
        v->addStretch();
        gmain->addLayout(v, 0, 0);
    }

    {
        m_lbl_video = new QLabel();
        m_showWidget = new QStackedWidget();
        m_showWidget->addWidget(m_LineDataPanel);
        m_showWidget->addWidget(m_lbl_video);
        m_showWidget->setCurrentIndex(0);

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_showWidget, 1);
        gmain->addLayout(v, 0, 1);
    }

    gmain->setColumnStretch(0, 1);
    gmain->setColumnStretch(1, 5);
    setLayout(gmain);

    connect(this, &MainWidget::evtCallback, this, [this](unsigned nEvent)
    {
        /* this run in the UI thread */
        if (m_hcam)
        {
            if (ALTAIRCAM_EVENT_IMAGE == nEvent)
                handleImageEvent();
            else if (ALTAIRCAM_EVENT_EXPOSURE == nEvent)
                handleExpoEvent();
            else if (ALTAIRCAM_EVENT_STILLIMAGE == nEvent)
                handleStillImageEvent();
            else if (ALTAIRCAM_EVENT_ERROR == nEvent)
            {
                closeCamera();
                QMessageBox::warning(this, "Warning", "Generic error.");
            }
            else if (ALTAIRCAM_EVENT_DISCONNECTED == nEvent)
            {
                closeCamera();
                QMessageBox::warning(this, "Warning", "Camera disconnect.");
            }
        }
    });

    connect(m_timer, &QTimer::timeout, this, [this]()
    {
        unsigned nFrame = 0, nTime = 0, nTotalFrame = 0;
        if (m_hcam && SUCCEEDED(Altaircam_get_FrameRate(m_hcam, &nFrame, &nTime, &nTotalFrame)) && (nTime > 0))
            m_lbl_frame->setText(QString::asprintf("%u, fps = %.1f", nTotalFrame, nFrame * 1000.0 / nTime));
        short nTemperature = 0;
        if (m_hcam && SUCCEEDED(Altaircam_get_Temperature(m_hcam, &nTemperature)))
            m_lbl_Temperature->setText(QString::asprintf("%.1f", nTemperature / 10.0f));
    });
}

void MainWidget::closeCamera()
{
    if (m_hcam)
    {
        Altaircam_Close(m_hcam);
        m_hcam = nullptr;
    }
    delete[] m_pData;
    m_pData = nullptr;
    delete[] m_pshowData;
    m_pshowData = nullptr;

    m_btn_open->setText("Open");
    m_timer->stop();
    m_lbl_frame->clear();
    m_sld_expoTime->setEnabled(false);
    m_btn_expoTime->setEnabled(false);
    m_edt_expoTime->setEnabled(false);
    m_btn_snap->setEnabled(false);
    m_cmb_res->setEnabled(false);
    m_cmb_res->clear();
    m_rdo_video->setEnabled(false);
    m_rdo_trigger->setEnabled(false);
    m_btn_trigger->setEnabled(false);
    m_cmb_triggerSource->setEnabled(false);
    m_rdo_OpenTEC->setEnabled(false);
    m_rdo_CloseTEC->setEnabled(false);
    m_sld_frameRate->setEnabled(false);
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
    m_pData = new ushort[m_imgWidth * m_imgHeight];
    if (m_pshowData)
    {
        delete[] m_pshowData;
        m_pshowData = nullptr;
    }
    m_pshowData = new uchar[m_imgWidth * m_imgHeight];
    unsigned uimax = 0, uimin = 0, uidef = 0;
    Altaircam_get_ExpTimeRange(m_hcam, &uimin, &uimax, &uidef);
    m_sld_expoTime->setRange(uimin / 1000, uimax / 1000);
    handleExpoEvent();

    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_BITDEPTH, 1);
    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_RAW, 1);
    Altaircam_put_AutoExpoEnable(m_hcam, 0);
    if (SUCCEEDED(Altaircam_StartPullModeWithCallback(m_hcam, eventCallBack, this)))
    {
        m_rdo_video->setChecked(true);
        m_cmb_res->setEnabled(true);
        m_btn_open->setText("Close");
        m_btn_snap->setEnabled(true);
        m_btn_expoTime->setEnabled(true);
        m_edt_expoTime->setEnabled(true);
        m_sld_expoTime->setEnabled(true);
        m_rdo_video->setEnabled(true);
        m_rdo_trigger->setEnabled(true);
        m_timer->start(1000);

        int bTEC;
        if (SUCCEEDED(Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_TEC, &bTEC)))
        {
            m_btn_setTEC->setEnabled(true);
            m_rdo_OpenTEC->setEnabled(true);
            m_rdo_CloseTEC->setEnabled(true);
            m_edt_TECtarget->setEnabled(true);
            if (bTEC == 1)
                m_rdo_OpenTEC->setChecked(true);
            else
                m_rdo_CloseTEC->setChecked(true);
            Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_TECTARGET, -300);
            int nTECtarget;
            if (SUCCEEDED(Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_TECTARGET, &nTECtarget)))
                m_edt_TECtarget->setText(QString::number(nTECtarget / 10.0));
        }

        if (m_cur.model->flag & ALTAIRCAM_FLAG_PRECISE_FRAMERATE)
        {
            m_sld_frameRate->setEnabled(true);
            int nMaxFramerate, nMinFramerate, nFramerate;
            Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_MAX_PRECISE_FRAMERATE, &nMaxFramerate);
            Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_MIN_PRECISE_FRAMERATE, &nMinFramerate);
            Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_PRECISE_FRAMERATE, &nFramerate);
            m_sld_frameRate->setRange(nMinFramerate, nMaxFramerate);
            m_sld_frameRate->setValue(nFramerate);
        }
        else
        {
            m_sld_frameRate->setEnabled(false);
        }

        int maxdepth = Altaircam_get_MaxBitDepth(m_hcam);
        m_rdo_highBit->setText(QString::number(maxdepth));
        m_rdo_highBit->setEnabled(true);
        m_rdo_lowBit->setEnabled(true);
        Altaircam_get_RawFormat(m_hcam, &m_pFourCC, &m_depth);
        if (m_depth == 8)
            m_rdo_lowBit->setChecked(true);
        else
            m_rdo_highBit->setChecked(true);
    }
    else
    {
        closeCamera();
        QMessageBox::warning(this, "Warning", "Failed to start camera.");
    }
}

void MainWidget::openCamera()
{
    m_hcam = Altaircam_Open(m_cur.id);
    if (m_hcam)
    {
        Altaircam_get_eSize(m_hcam, (unsigned*)&m_res);
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

        Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_BYTEORDER, 0); //Qimage use RGB byte order
        Altaircam_put_AutoExpoEnable(m_hcam, 1);
        startCamera();
    }
}

void MainWidget::onBtnOpen()
{
    if (m_hcam)
        closeCamera();
    else
    {
        AltaircamDeviceV2 arr[ALTAIRCAM_MAX] = { 0 };
        unsigned count = Altaircam_EnumV2(arr);
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
            Altaircam_Snap(m_hcam, 0xffff);
        else
        {
            QMenu menu;
            for (unsigned i = 0; i < m_cur.model->still; ++i)
            {
                menu.addAction(QString::asprintf("%u*%u", m_cur.model->res[i].width, m_cur.model->res[i].height), this, [this, i](bool)
                {
                    Altaircam_Snap(m_hcam, i);
                });
            }
            menu.exec(mapToGlobal(m_btn_snap->pos()));
        }
    }
}

void MainWidget::onBtnTrigger()
{
    if (m_hcam)
        Altaircam_Trigger(m_hcam, 1);
}

void MainWidget::onBtnSetTEC()
{
    if (m_hcam)
    {
        int val = int(m_edt_TECtarget->text().toFloat() * 10);
        Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_TECTARGET, val);
    }
}

void MainWidget::onRdoTrigger()
{
    m_cmb_triggerSource->setEnabled(true);
    int index;
    Altaircam_IoControl(m_hcam, 0, ALTAIRCAM_IOCONTROLTYPE_GET_TRIGGERSOURCE, NULL, &index);
    m_cmb_triggerSource->setCurrentIndex(index);
    m_btn_trigger->setEnabled(index == 5 ? true : false);

    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_TRIGGER, 2);
}

void MainWidget::onRdoVideo()
{
    m_cmb_triggerSource->setEnabled(false);
    m_btn_trigger->setEnabled(false);
    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_TRIGGER, 0);
}

void MainWidget::onRdoOpenTEC()
{
    m_btn_setTEC->setEnabled(true);
    m_edt_TECtarget->setEnabled(true);
    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_TEC, 1);
}

void MainWidget::onRdoCloseTEC()
{
    m_btn_setTEC->setEnabled(false);
    m_edt_TECtarget->setEnabled(false);
    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_TEC, 0);
}

void MainWidget::onRdoPoly()
{
    m_bshow = 0;
    m_showWidget->setCurrentIndex(0);
}

void MainWidget::onRdoImage()
{
    m_bshow = 1;
    m_showWidget->setCurrentIndex(1);
}

void MainWidget::onRdoLowBit()
{
    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_BITDEPTH, 0);
    Altaircam_get_RawFormat(m_hcam, &m_pFourCC, &m_depth);
    if (m_cur.model->flag & ALTAIRCAM_FLAG_PRECISE_FRAMERATE)
    {
        int nMaxFramerate, nMinFramerate, nFramerate;
        Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_MAX_PRECISE_FRAMERATE, &nMaxFramerate);
        Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_MIN_PRECISE_FRAMERATE, &nMinFramerate);
        Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_PRECISE_FRAMERATE, &nFramerate);
        m_sld_frameRate->setRange(nMinFramerate, nMaxFramerate);
        m_sld_frameRate->setValue(nFramerate);
    }
}

void MainWidget::onRdoHighBit()
{
    Altaircam_put_Option(m_hcam, ALTAIRCAM_OPTION_BITDEPTH, 1);
    Altaircam_get_RawFormat(m_hcam, &m_pFourCC, &m_depth);
    if (m_cur.model->flag & ALTAIRCAM_FLAG_PRECISE_FRAMERATE)
    {
        int nMaxFramerate, nMinFramerate, nFramerate;
        Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_MAX_PRECISE_FRAMERATE, &nMaxFramerate);
        Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_MIN_PRECISE_FRAMERATE, &nMinFramerate);
        Altaircam_get_Option(m_hcam, ALTAIRCAM_OPTION_PRECISE_FRAMERATE, &nFramerate);
        m_sld_frameRate->setRange(nMinFramerate, nMaxFramerate);
        m_sld_frameRate->setValue(nFramerate);
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
    if (SUCCEEDED(Altaircam_PullImage(m_hcam, m_pData, 0, &width, &height)))
    {
        if (m_bshow == 1)
        {
            if (m_depth > 8)
            {
                ushort* pdata = reinterpret_cast<ushort*>(m_pData);
                const int bitShift = m_depth - 8;
                for (unsigned y = 0; y < height; ++y)
                {
                    unsigned index = y * width;
                    for (unsigned x = 0; x < width; ++x)
                        m_pshowData[index + x] = pdata[index + x] >> bitShift;
                }

                QImage image(m_pshowData, width, height, QImage::Format_Grayscale8);
                QImage newimage = image.scaled(m_lbl_video->width(), m_lbl_video->height(), Qt::KeepAspectRatio, Qt::FastTransformation);
                m_lbl_video->setPixmap(QPixmap::fromImage(newimage));
            }
            else
            {
                uchar* pdata = reinterpret_cast<uchar*>(m_pData);
                QImage image(pdata, width, height, QImage::Format_Grayscale8);
                QImage newimage = image.scaled(m_lbl_video->width(), m_lbl_video->height(), Qt::KeepAspectRatio, Qt::FastTransformation);
                m_lbl_video->setPixmap(QPixmap::fromImage(newimage));
            }
        }
        else
        {
            if (m_depth > 8)
                m_LineDataPanel->OnBufferPL16(reinterpret_cast<ushort*>(m_pData), width, m_depth);
            else
                m_LineDataPanel->OnBufferPL8(reinterpret_cast<uchar*>(m_pData), width, m_depth);
        }
    }
}

void MainWidget::handleExpoEvent()
{
    unsigned time = 0;
    Altaircam_get_ExpoTime(m_hcam, &time);
    time /= 1000;
    {
        const QSignalBlocker blocker(m_sld_expoTime);
        m_sld_expoTime->setValue(int(time));
    }
    {
        const QSignalBlocker blocker(m_edt_expoTime);
        m_edt_expoTime->setText(QString::number(time));
    }
}

void MainWidget::handleStillImageEvent()
{
    unsigned width = 0, height = 0;
    if (SUCCEEDED(Altaircam_PullStillImage(m_hcam, nullptr, 24, &width, &height))) // peek
    {
        std::vector<uchar> vec(TDIBWIDTHBYTES(width * 24) * height);
        if (SUCCEEDED(Altaircam_PullStillImage(m_hcam, &vec[0], 24, &width, &height)))
        {
            QImage image(&vec[0], width, height, QImage::Format_RGB888);
            image.save(QString::asprintf("demolinecam_%u.jpg", ++m_count));
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
    Altaircam_GigeEnable(nullptr, nullptr);
    QApplication a(argc, argv);
    MainWidget mw;
    mw.show();
    return a.exec();
}
