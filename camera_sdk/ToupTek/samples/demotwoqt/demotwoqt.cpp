#include <QApplication>
#include "demotwoqt.h"

MainWidget::MainWidget(QWidget* parent)
    : QWidget(parent), m_timer(new QTimer(this))
{
    setMinimumSize(1024, 768);
    memset(m_hcam, 0, sizeof(m_hcam));
    memset(m_lbl_video, 0, sizeof(m_lbl_video));
    memset(m_lbl_frame, 0, sizeof(m_lbl_frame));
    memset(m_imgWidth, 0, sizeof(m_imgWidth));
    memset(m_imgHeight, 0, sizeof(m_imgHeight));
    memset(m_pData, 0, sizeof(m_pData));

    QHBoxLayout* hlayout = new QHBoxLayout(this);
    {
        QVBoxLayout* vlayout = new QVBoxLayout();
        {
            m_btn_open = new QPushButton("Open");
            connect(m_btn_open, &QPushButton::clicked, this, &MainWidget::onBtnOpen);
            m_cbox_auto = new QCheckBox("Auto exposure");
            m_cbox_auto->setCheckState(Qt::Checked);
            connect(m_cbox_auto, &QCheckBox::stateChanged, this, [this](bool state)
            {
                if (m_hcam[0])
                    Toupcam_put_AutoExpoEnable(m_hcam[0], state ? 1 : 0);
                if (m_hcam[1])
                    Toupcam_put_AutoExpoEnable(m_hcam[1], state ? 1 : 0);
            });
            m_lbl_frame[0] = new QLabel();

            QVBoxLayout* v = new QVBoxLayout();
            v->addWidget(m_btn_open);
            v->addWidget(m_cbox_auto);
            v->addWidget(m_lbl_frame[0], 0, Qt::AlignBottom);
            vlayout->addLayout(v);
        }
        {
            m_lbl_frame[1] = new QLabel();
            vlayout->addWidget(m_lbl_frame[1], 0, Qt::AlignBottom);
        }
        hlayout->addLayout(vlayout, 1);
    }
    {
        m_lbl_video[0] = new QLabel();
        m_lbl_video[1] = new QLabel();

        QVBoxLayout* v = new QVBoxLayout();
        v->addWidget(m_lbl_video[0]);
        v->addWidget(m_lbl_video[1]);
        hlayout->addLayout(v, 4);
    }
    setLayout(hlayout);

    connect(this, &MainWidget::evtCallback, this, [this](int idx, unsigned nEvent)
    {
        /* this run in the UI thread */
        if (m_hcam[idx])
        {
            if (TOUPCAM_EVENT_IMAGE == nEvent)
                handleImageEvent(idx);
            else if (TOUPCAM_EVENT_ERROR == nEvent)
            {
                closeCamera(idx);
                QMessageBox::warning(this, "Warning", "Generic error.");
            }
            else if (TOUPCAM_EVENT_DISCONNECTED == nEvent)
            {
                closeCamera(idx);
                QMessageBox::warning(this, "Warning", "Camera disconnect.");
            }
        }
    });

    connect(m_timer, &QTimer::timeout, this, [this]()
    {
        unsigned nFrame = 0, nTime = 0, nTotalFrame = 0;
        if (m_hcam[0] && SUCCEEDED(Toupcam_get_FrameRate(m_hcam[0], &nFrame, &nTime, &nTotalFrame)) && (nTime > 0))
            m_lbl_frame[0]->setText(QString::asprintf("%u, fps = %.1f", nTotalFrame, nFrame * 1000.0 / nTime));
        if (m_hcam[1] && SUCCEEDED(Toupcam_get_FrameRate(m_hcam[1], &nFrame, &nTime, &nTotalFrame)) && (nTime > 0))
            m_lbl_frame[1]->setText(QString::asprintf("%u, fps = %.1f", nTotalFrame, nFrame * 1000.0 / nTime));
    });
    m_timer->start(1000);
}

void MainWidget::closeCamera(int idx)
{
    if (m_hcam[idx])
    {
        Toupcam_Close(m_hcam[idx]);
        m_hcam[idx] = nullptr;
    }
    delete[] m_pData[idx];
    m_pData[idx] = nullptr;
}

void MainWidget::closeEvent(QCloseEvent*)
{
    closeCamera(0);
    closeCamera(1);
}

void MainWidget::openCamera(int idx, const tchar* camId)
{
    m_hcam[idx] = Toupcam_Open(camId);
    if (m_hcam[idx])
    {
        Toupcam_get_Size(m_hcam[idx], &m_imgWidth[idx], &m_imgHeight[idx]);
        Toupcam_put_Option(m_hcam[idx], TOUPCAM_OPTION_BYTEORDER, 0); //Qimage use RGB byte order
        Toupcam_put_AutoExpoEnable(m_hcam[idx], m_cbox_auto->isChecked() ? 1 : 0);

        m_pData[idx] = new uchar[TDIBWIDTHBYTES(m_imgWidth[idx] * 24) * m_imgHeight[idx]];
        m_ctx[idx].pthis = this;
        m_ctx[idx].idx = idx;
        if (FAILED(Toupcam_StartPullModeWithCallback(m_hcam[idx], eventCallBack, &m_ctx[idx])))
        {
            closeCamera(idx);
            QMessageBox::warning(this, "Warning", "Failed to start camera.");
        }
    }
}

void MainWidget::onBtnOpen()
{
    if (m_hcam[0] || m_hcam[1])
        return;

    ToupcamDeviceV2 arr[TOUPCAM_MAX] = { 0 };
    const unsigned count = Toupcam_EnumV2(arr);
    if (0 == count)
        QMessageBox::warning(this, "Warning", "No camera found.");
    else
    {
        openCamera(0, arr[0].id);
        if (count > 1)
            openCamera(1, arr[1].id);
    }
}

void MainWidget::eventCallBack(unsigned nEvent, void* pCallbackCtx)
{
    callBackCtx* ctx = reinterpret_cast<callBackCtx*>(pCallbackCtx);
    emit ctx->pthis->evtCallback(ctx->idx, nEvent);
}

void MainWidget::handleImageEvent(int idx)
{
    unsigned width = 0, height = 0;
    if (SUCCEEDED(Toupcam_PullImage(m_hcam[idx], m_pData[idx], 24, &width, &height)))
    {
        QImage image(m_pData[idx], width, height, QImage::Format_RGB888);
        QImage newimage = image.scaled(m_lbl_video[idx]->width(), m_lbl_video[idx]->height(), Qt::KeepAspectRatio, Qt::FastTransformation);
        m_lbl_video[idx]->setPixmap(QPixmap::fromImage(newimage));
    }
}

int main(int argc, char* argv[])
{
    Toupcam_GigeEnable(nullptr, nullptr);
    QApplication a(argc, argv);
    MainWidget mw;
    mw.show();
    return a.exec();
}
