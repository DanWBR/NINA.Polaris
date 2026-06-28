#include "polylinepanel.h"
#include <QSettings>
#include <QFileDialog>
#include <array>
#include <QColor>
#include "global.h"

CLineDataCtrl::CLineDataCtrl()
    : m_zoomMax(10000), m_bitdepth(0), m_bMove(false), m_data(nullptr), m_aPoint(nullptr), m_dtmp(nullptr)
    , m_bMouseHover(false)
{
    setMouseTracking(true);
    m_curpt.setX(INT_MIN);
    m_curpt.setY(INT_MIN);
    m_zoomX = m_zoomY = 100;
    m_offset.setX(0);
    m_offset.setY(0);
    m_lastpt.setX(0);
    m_lastpt.setY(0);
    m_oldm_offset.setX(0);
    m_oldm_offset.setY(0);

    QPainter painter(this);
    m_nTextHeight = painter.fontMetrics().height() + DpiScale(4);
    connect(this, &CLineDataCtrl::Update, this, &CLineDataCtrl::onUpdate);
}

void CLineDataCtrl::Reset(int bitdepth, int width)
{
    m_bitdepth = bitdepth;
    m_width = width;
    {
        for (auto item : m_free)
            SAFE_FREE(item);
        for (auto item : m_used)
            SAFE_FREE(item);
        SAFE_FREE(m_data);
        m_used.clear();
        m_free.clear();
        SAFE_FREE(m_aPoint);
        SAFE_FREE(m_dtmp);

        if (m_bitdepth)
        {
            const unsigned newSize = m_width;
            m_aPoint = (QPoint*)malloc(sizeof(QPoint) * newSize);
            m_dtmp = (double*)malloc(sizeof(double) * newSize);
            for (int i = 0; i < 4; ++i)
                m_free.push_back((unsigned*)malloc(newSize * sizeof(unsigned)));
        }
    }

    Zoom11();
}

template<typename T>
void CLineDataCtrl::OnBuffer(T* pRaw, int width, int bitdepth)
{
    unsigned* pData = nullptr;
    {
        QMutexLocker ul(&m_cs);
        if ((width != m_width) || (bitdepth != m_bitdepth))
            Reset(bitdepth, width);
        if (!m_free.empty())
        {
            pData = m_free.front();
            m_free.pop_front();
        }
    }
    if (pData)
    {
        for (int i = 0; i < width; i++)
            pData[i] = pRaw[i];
        {
            QMutexLocker ul(&m_cs);
            m_used.push_back(pData);
        }
        emit Update();
    }
}

void CLineDataCtrl::onUpdate()
{
    update();
}

QRect CLineDataCtrl::GetHistRect()const
{
    QRect rcHist = this->rect();
    rcHist.setTop(rcHist.top() + m_nTextHeight);
    rcHist.setBottom(rcHist.bottom() - m_nTextHeight);
    return rcHist;
}

static int LineDataCalcStep(int a, int b)
{
    const double r = DpiUnscale(100) * ((double)a) / b;
    if (r <= 2.0)
        return 10000;
    if (r <= 4.0)
        return 4000;
    if (r <= 5 * 2.0)
        return 2000;
    if (r <= 25 * 2.0)
        return 1000;
    if (r <= 40 * 2.0)
        return 400;
    if (r <= 100 * 2.0)
        return 200;
    if (r <= 200 * 2.0)
        return 100;
    if (r <= 400 * 2.0)
        return 40;
    if (r <= 500 * 2.0)
        return 20;
    if (r <= 5000 * 2.0)
        return 10;
    if (r <= 10000 * 2.0)
        return 4;
    return 1;
}

void CLineDataCtrl::DrawXAxis(QPainter* hMemDC, const QRect& rcHist, const unsigned newSize)
{
    const int step = LineDataCalcStep(m_zoomX * rcHist.width(), 100 * newSize);
    bool bLast = false;
    QString text;
    QRect rcText(0, rcHist.bottom() + DpiScale(4), 0, rcHist.bottom() + m_nTextHeight);
    if (0 == m_offset.x())
    {
        rcText.setLeft(rcHist.left() + 2);
        rcText.setRight(rcHist.right());
        hMemDC->drawText(rcText, Qt::AlignLeft | Qt::TextSingleLine, "0");
    }
    if (m_offset.x() + rcHist.width() >= rcHist.width() * m_zoomX / 100)
    {
        rcText.setLeft(rcHist.left());
        rcText.setRight(rcHist.right() - 2);
        text = QString::number(newSize - 1);
        hMemDC->drawText(rcText, Qt::AlignRight | Qt::TextSingleLine, text);
        bLast = true;
    }
    for (unsigned i = step; i < newSize; i += step)
    {
        const int x = rcHist.left() + (rcHist.right() - rcHist.left()) * i / (100.0 * newSize / m_zoomX) - m_offset.x();
        if ((x >= rcHist.left()) && (x <= rcHist.right()))
        {
            hMemDC->drawLine(x, rcHist.bottom(), x, rcHist.bottom() + DpiScale(4));
            if (bLast && (i + step >= newSize))
            {
                QRect tRect;
                text = QString::number(newSize - 1);
                hMemDC->drawText(tRect, Qt::TextSingleLine | Qt::TextDontPrint, text);
                if (x + 100 + tRect.width() >= rcHist.right())
                    break;
            }
            text = QString::number(i);
            rcText.setLeft(x - 100);
            rcText.setRight(x + 100);
            hMemDC->drawText(rcText, Qt::AlignHCenter | Qt::TextSingleLine, text);
        }
    }
}

static void calcValue(const unsigned* data, unsigned size, unsigned& uMax, unsigned& uMin, double& dAvg)
{
    double sum = 0;
    uMax = 0;
    uMin = 0xfffff;
    for (unsigned i = 0; i < size; ++i)
    {
        unsigned val = data[i];
        sum += val;

        if (val < uMin) uMin = val;
        if (val > uMax) uMax = val;
    }
    sum /= size;
    dAvg = sum;
}

bool CLineDataCtrl::calcXY(unsigned& X, unsigned& Y)const
{
    if ((m_curpt.x() > 0) && (m_curpt.y() > 0))
    {
        const unsigned newSize = m_width;
        const QRect rcHist = GetHistRect();
        if ((m_curpt.x() > rcHist.left()) && (m_curpt.x() < rcHist.right()) && (m_curpt.y() > rcHist.top()) && (m_curpt.y() < rcHist.bottom()))
        {
            X = (m_curpt.x() + m_offset.x() - rcHist.left()) * (100 * newSize) / (m_zoomX * rcHist.width());
            if ((X >= 0) && (X < newSize))
            {
                Y = m_data[X];
                return true;
            }
        }
    }
    return false;
}

void CLineDataCtrl::DrawValue(QPainter* hMemDC, const QRect& rcClient)
{
    const unsigned newSize = m_width;
    unsigned uMax = 0, uMin = 0, X, Y;
    double dAvg = 0;
    QString str;
    const bool bXY = calcXY(X, Y);
    calcValue(m_data, newSize, uMax, uMin, dAvg);
    if (bXY)
        str = QString::asprintf("max: %u; min: %u; avg: %.2f; X: %u, Y: %u", uMax, uMin, dAvg, X, Y);
    else
        str = QString::asprintf("max: %u; min: %u; avg: %.2f", uMax, uMin, dAvg);
    QRect rc = { rcClient.left(), rcClient.top(), rcClient.right(), rcClient.top() + m_nTextHeight };
    hMemDC->drawText(rc, Qt::AlignHCenter | Qt::TextSingleLine | Qt::TextHideMnemonic, str);
}

void CLineDataCtrl::paintEvent(QPaintEvent*)
{
    QPainter painter(this);
    const QRect rcHist = GetHistRect();
    const QRect rcTotal = this->rect();
    QRect rcClient{ rcTotal.left(), rcTotal.top(), rcTotal.right(), m_nTextHeight };
    const int newSize = m_width;
    const long nWidth = rcHist.right() - rcHist.left();
    const long nHeight = rcHist.bottom() - rcHist.top();
    if (nWidth <= 100)
        return;

    QImage image(rcTotal.size(), QImage::Format_ARGB32);
    image.fill(Qt::white);
    QPainter imagePainter(&image);
    imagePainter.setRenderHint(QPainter::Antialiasing);

    {
        QMutexLocker ul(&m_cs);
        if (!m_used.empty())
        {
            if (m_data)
                m_free.push_back(m_data);
            m_data = m_used.front();
            m_used.pop_front();
        }
    }

    if (m_data && (rcHist.width() >= DpiScale(16)))
    {
        QColor clrPen = palette().text().color();
        bool bHasPoint = false;
        const unsigned dMaxY = 1 << m_bitdepth;//*std::max_element(m_data, m_data + newSize);
        bHasPoint = true;
        for (int i = 0; i < newSize; ++i)
        {
            m_aPoint[i].setX(rcHist.left() + MulDiv(i * m_zoomX, nWidth, newSize * 100) - m_offset.x());
            m_aPoint[i].setY(rcHist.bottom() - MulDiv(nHeight, m_data[i] * m_zoomY, dMaxY * 100) + m_offset.y() - 1);
        }

        if (bHasPoint)
        {
            imagePainter.setClipRect(rcHist);
            imagePainter.drawPolyline(m_aPoint, newSize);
            imagePainter.setClipping(false);
        }

        if (rcHist.width() > DpiScale(100))
        {
            imagePainter.setPen(palette().text().color());
            imagePainter.setBackgroundMode(Qt::TransparentMode);
            DrawXAxis(&imagePainter, rcHist, newSize);
            DrawValue(&imagePainter, rcClient);
        }
    }

    imagePainter.setPen(palette().text().color());
    imagePainter.setBrush(Qt::NoBrush);
    imagePainter.drawRect(rcHist);
    painter.drawImage(rcTotal, image);
}

void CLineDataCtrl::mouseMoveEvent(QMouseEvent* event)
{
    QPoint point = event->pos();
    if (m_bMove)
    {
        m_offset.setX(m_oldm_offset.x() - (point.x() - m_lastpt.x()));
        m_offset.setY(m_oldm_offset.y() + (point.y() - m_lastpt.y()));
        RecalcSize();
        m_curpt = point;
        emit Update();
    }
    else
    {
        if (!m_bMouseHover)
            m_bMouseHover = true;
        m_curpt = point;
        emit Update();
    }
}

void CLineDataCtrl::mouseReleaseEvent(QMouseEvent* event)
{
    if (event->button() == Qt::LeftButton && m_bMove)
    {
        m_bMove = false;
        setCursor(QCursor(Qt::ArrowCursor));

        m_offset.setX(m_oldm_offset.x() - (event->pos().x() - m_lastpt.x()));
        m_offset.setY(m_oldm_offset.y() + (event->pos().y() - m_lastpt.y()));

        RecalcSize();
        emit Update();
    }
    QWidget::mouseReleaseEvent(event);
}

void CLineDataCtrl::mousePressEvent(QMouseEvent* event)
{
    if (event->button() == Qt::LeftButton)
    {
        const QRect rectHist = GetHistRect();
        if (rectHist.contains(event->pos()))
        {
            m_bMove = true;
            m_lastpt = event->pos();
            m_oldm_offset = m_offset;
            setCursor(QCursor(Qt::ClosedHandCursor));
        }
    }
    QWidget::mousePressEvent(event);
}

void CLineDataCtrl::resizeEvent(QResizeEvent* event)
{
    QWidget::resizeEvent(event);
    RecalcSize();
    emit Update();
}

void CLineDataCtrl::leaveEvent(QEvent* event)
{
    m_curpt = QPoint(INT_MIN, INT_MIN);
    if (m_bMouseHover)
    {
        m_bMouseHover = false;
        emit Update();
    }
    QWidget::leaveEvent(event);
}

void CLineDataCtrl::ZoomOut(bool bX)
{
    if (CanZoomOut(bX))
    {
        const QRect HistRect = GetHistRect();
        if (bX)
        {
            int newZoom = m_zoomX * 2 / 3;
            if (newZoom < 100)
                newZoom = 100;
            if (newZoom == m_zoomX)
                return;

            const LONG x = MulDiv(m_offset.x() + HistRect.width() / 2, 100, m_zoomX);
            m_zoomX = newZoom;
            m_offset.setX(MulDiv(x, m_zoomX, 100) - HistRect.width() / 2);
        }
        else
        {
            int newZoom = m_zoomY * 2 / 3;
            if (newZoom < 100)
                newZoom = 100;
            if (newZoom == m_zoomY)
                return;

            const LONG y = MulDiv(HistRect.height() / 2 + m_offset.y(), 100, m_zoomY);
            m_zoomY = newZoom;
            m_offset.setY(MulDiv(y, m_zoomY, 100) - HistRect.height() / 2);
        }
        RecalcSize();
        update();
    }
}

void CLineDataCtrl::ZoomIn(bool bX)
{
    if (CanZoomIn(bX))
    {
        const QRect HistRect = GetHistRect();
        if (bX)
        {
            int newZoom = m_zoomX * 3 / 2;
            if (newZoom > m_zoomMax)
                newZoom = m_zoomMax;
            if (newZoom == m_zoomX)
                return;

            const LONG x = MulDiv(m_offset.x() + HistRect.width() / 2, 100, m_zoomX);
            m_zoomX = newZoom;
            m_offset.setX(MulDiv(x, m_zoomX, 100) - HistRect.width() / 2);
        }
        else
        {
            int newZoom = m_zoomY * 3 / 2;
            if (newZoom > m_zoomMax)
                newZoom = m_zoomMax;
            if (newZoom == m_zoomY)
                return;

            const LONG y = MulDiv(HistRect.height() / 2 + m_offset.y(), 100, m_zoomY);
            m_zoomY = newZoom;
            m_offset.setY(MulDiv(y, m_zoomY, 100) - HistRect.height() / 2);
        }
        RecalcSize();
        update();
    }
}

bool CLineDataCtrl::CanZoomOut(bool bX)const
{
    if (m_data)
    {
        if (bX)
        {
            if (m_zoomX > 100)
                return true;
        }
        else
        {
            if (m_zoomY > 100)
                return true;
        }
    }
    return false;
}

bool CLineDataCtrl::CanZoomIn(bool bX)const
{
    if (m_data)
    {
        if (bX)
        {
            if (m_zoomX < m_zoomMax)
                return true;
        }
        else
        {
            if (m_zoomY < m_zoomMax)
                return true;
        }
    }
    return false;
}

bool CLineDataCtrl::HasData()const
{
    return (m_data && m_bitdepth) ? true : false;
}

void CLineDataCtrl::Zoom11()
{
    m_zoomX = m_zoomY = 100;
    m_offset.setX(0);
    m_offset.setY(0);
    emit Update();
}

void CLineDataCtrl::RecalcSize()
{
    const QRect rectHist = GetHistRect();
    const LONG maxy = MulDiv(rectHist.height(), m_zoomY, 100) - rectHist.height();
    const LONG maxx = MulDiv(rectHist.width(), m_zoomX, 100) - rectHist.width();
    if (m_offset.x() < 0)
        m_offset.setX(0);
    else if (m_offset.x() > maxx)
        m_offset.setX(maxx);

    if (m_offset.y() < 0)
        m_offset.setY(0);
    else if (m_offset.y() > maxy)
        m_offset.setY(maxy);
}

void CLineDataCtrl::Export(FILE* fp)
{
    if (m_bitdepth && m_data)
    {
        const unsigned newSize = m_width;
        for (unsigned i = 0; i < newSize; ++i)
            fprintf(fp, "%u\n", m_data[i]);
    }
}

CLineDataOptionDlg::CLineDataOptionDlg(CLineDataCtrl& ctrl, QWidget* parent) :
    QDialog(parent), m_ctrl(ctrl)
{
    setWindowTitle("Set");
    setWindowFlags(windowFlags() & ~Qt::WindowContextHelpButtonHint);

    QVBoxLayout* vlyt_set = new QVBoxLayout();

    {
        m_btn_ok = new QPushButton("OK");
        connect(m_btn_ok, &QPushButton::clicked, this, &CLineDataOptionDlg::onOkButtonClicked);
        vlyt_set->addWidget(m_btn_ok);
    }

    setLayout(vlyt_set);
}

void CLineDataOptionDlg::onOkButtonClicked()
{
    close();
}

LineDataToolBar::LineDataToolBar(QWidget* parent) : QToolBar(parent)
{
    setOrientation(Qt::Vertical);
    setIconSize(QSize(DpiScale(16), DpiScale(16)));
    setupActions();
}

void LineDataToolBar::enableButton(int actionId, bool enable)
{
    if (actionsMap.contains(actionId))
        actionsMap[actionId]->setEnabled(enable);
}

void LineDataToolBar::setupActions()
{
    QPixmap iconSheet(":/images/toolbarhistogram.png");
    iconSheet = iconSheet.scaled(DpiScale(16) * 8, DpiScale(16));
    addAction(QIcon(iconSheet.copy(0 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "Pause", ID_m_toolbarLineData_PAUSE);
    addAction(QIcon(iconSheet.copy(1 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "Option", ID_LineData_OPTION);
    addAction(QIcon(iconSheet.copy(2 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "1:1", ID_LineData_m_toolbarBESTFIT);
    addAction(QIcon(iconSheet.copy(3 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "X Zoom In", ID_ZOOMIN_X);
    addAction(QIcon(iconSheet.copy(4 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "X Zoom Out", ID_ZOOMOUT_X);
    addAction(QIcon(iconSheet.copy(5 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "Y Zoom In", ID_ZOOMIN_Y);
    addAction(QIcon(iconSheet.copy(6 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "Y Zoom Out", ID_ZOOMOUT_Y);
    addAction(QIcon(iconSheet.copy(7 * DpiScale(16), 0, DpiScale(16), DpiScale(16))), "Export", ID_LineData_EXPORT);
}

void LineDataToolBar::addAction(QIcon icon, const QString& tooltip, int id)
{
    QAction* action = new QAction(icon, "", this);
    action->setToolTip(tooltip);
    QToolBar::addAction(action);
    actionsMap[id] = action;

    connect(action, &QAction::triggered, this, [this, id]() { this->actionTriggered(id); });
}

void LineDataToolBar::actionTriggered(int id)
{
    switch (id) {
    case ID_LineData_OPTION:
        emit LineDataOption();
        break;
    case ID_ZOOMIN_X:
        emit ZoomInX();
        break;
    case ID_ZOOMOUT_X:
        emit ZoomOutX();
        break;
    case ID_ZOOMIN_Y:
        emit ZoomInY();
        break;
    case ID_ZOOMOUT_Y:
        emit ZoomOutY();
        break;
    case ID_LineData_m_toolbarBESTFIT:
        emit ZoomBestfit();
        break;
    case ID_m_toolbarLineData_PAUSE:
        emit LineDataPause();
        break;
    case ID_LineData_EXPORT:
        emit LineDataExport();
        break;
    case ID_LineData_LEVEL_RANGE:
        emit LevelRange();
        break;
    default:
        break;
    }
}

LineDataPanel::LineDataPanel(QWidget* parent)
    : QWidget(parent), m_bShow(false), m_bPause(false)
{
    setObjectName("LineDataPanel");
    m_ctrl = new CLineDataCtrl();
    m_toolbar = new LineDataToolBar(this);
    QHBoxLayout* layout = new QHBoxLayout(this);
    layout->addWidget(m_toolbar);
    layout->addWidget(m_ctrl, 1);
    this->setLayout(layout);
    connect(m_toolbar, &LineDataToolBar::LineDataOption, this, &LineDataPanel::OnLineDataOption);
    connect(m_toolbar, &LineDataToolBar::ZoomInX, this, &LineDataPanel::OnZoomInX);
    connect(m_toolbar, &LineDataToolBar::ZoomInY, this, &LineDataPanel::OnZoomInY);
    connect(m_toolbar, &LineDataToolBar::ZoomOutX, this, &LineDataPanel::OnZoomOutX);
    connect(m_toolbar, &LineDataToolBar::ZoomOutY, this, &LineDataPanel::OnZoomOutY);
    connect(m_toolbar, &LineDataToolBar::ZoomBestfit, this, &LineDataPanel::OnZoomBestfit);
    connect(m_toolbar, &LineDataToolBar::LineDataPause, this, &LineDataPanel::OnLineDataPause);
    connect(m_toolbar, &LineDataToolBar::LineDataExport, this, &LineDataPanel::OnLineDataExport);
}

void LineDataPanel::OnLineDataOption()
{
    CLineDataOptionDlg LineDataOptionDlg(*m_ctrl);
    LineDataOptionDlg.exec();
}

void LineDataPanel::OnZoomInX()
{
    m_ctrl->ZoomIn(true);
}

void LineDataPanel::OnZoomOutX()
{
    m_ctrl->ZoomOut(true);
}

void LineDataPanel::OnZoomInY()
{
    m_ctrl->ZoomIn(false);
}

void LineDataPanel::OnZoomOutY()
{
    m_ctrl->ZoomOut(false);
}

void LineDataPanel::OnZoomBestfit()
{
    m_ctrl->Zoom11();
}

void LineDataPanel::OnLineDataPause()
{
    m_bPause = !m_bPause;
}

void LineDataPanel::OnLineDataExport()
{
    QString filePath = QFileDialog::getSaveFileName(this, "Save", "", "Text Files (*.csv)");
    if (!filePath.isEmpty())
    {
        FILE* file = fopen(filePath.toStdString().c_str(), "w");
        if (file)
        {
            m_ctrl->Export(file);
            fclose(file);
        }
    }
}

void LineDataPanel::OnBufferPL16(ushort* pRaw, int width, int bitdepth)
{
    if (!m_bPause)
        m_ctrl->OnBuffer(pRaw, width, bitdepth);
}

void LineDataPanel::OnBufferPL8(uchar* pRaw, int width, int bitdepth)
{
    if (!m_bPause)
        m_ctrl->OnBuffer(pRaw, width, bitdepth);
}
