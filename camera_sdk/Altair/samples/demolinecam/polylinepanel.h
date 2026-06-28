#ifndef POLYLINEPANEL_H
#define POLYLINEPANEL_H

#include <deque>
#include <QMutex>
#include <QPainter>
#include <QDialog>
#include <QComboBox>
#include <QCheckBox>
#include <QToolBar>
#include <QMouseEvent>

class CLineDataCtrl : public QWidget
{
    Q_OBJECT
public:
    CLineDataCtrl();
    void ZoomOut(bool bX);
    void ZoomIn(bool bX);
    bool CanZoomOut(bool bX)const;
    bool CanZoomIn(bool bX)const;
    bool HasData()const;
    void Zoom11();
    void Export(FILE* fp);
    template<typename T>
    void OnBuffer(T* pRaw, int width, int bitdepth);

protected:
    void paintEvent(QPaintEvent*) override;
    void mouseMoveEvent(QMouseEvent* event) override;
    void mouseReleaseEvent(QMouseEvent* event) override;
    void mousePressEvent(QMouseEvent* event) override;
    void resizeEvent(QResizeEvent* event) override;
    void leaveEvent(QEvent* event) override;

signals:
    void Update();

private slots:
    void onUpdate();

private:
    void Reset(int bitdepth, int width);
    QRect GetHistRect()const;
    bool calcXY(unsigned& X, unsigned& Y)const;
    void RecalcSize();
    void DrawXAxis(QPainter* hMemDC, const QRect& rcHist, const unsigned newSize);
    void DrawValue(QPainter* hMemDC, const QRect& rcClient);

    const int				m_zoomMax;
    int						m_nTextHeight;
    int     				m_bitdepth;
    int     				m_width;
    bool					m_bMove;
    bool					m_bMouseHover;
    QPoint*					m_aPoint;
    double*					m_dtmp;
    unsigned*				m_data;
    std::deque<unsigned*>	m_free, m_used;
    QMutex             		m_cs;
    int						m_zoomX, m_zoomY;
    QPoint 					m_offset, m_lastpt, m_oldm_offset;
    QPoint 					m_curpt;
};

enum ActionIds {
    ID_LineData_OPTION,
    ID_ZOOMIN_X,
    ID_ZOOMOUT_X,
    ID_ZOOMIN_Y,
    ID_ZOOMOUT_Y,
    ID_LineData_m_toolbarBESTFIT,
    ID_m_toolbarLineData_PAUSE,
    ID_LineData_EXPORT,
    ID_LineData_LEVEL_RANGE
};

class LineDataToolBar : public QToolBar
{
    Q_OBJECT
public:
    explicit LineDataToolBar(QWidget* parent = nullptr);
    void enableButton(int actionId, bool enable);

private:
    QMap<int, QAction*> actionsMap;
    void setupActions();
    void addAction(QIcon icon, const QString& tooltip, int id);
private slots:
    void actionTriggered(int id);

signals:
    void LineDataOption();
    void ZoomInX();
    void ZoomOutX();
    void ZoomInY();
    void ZoomOutY();
    void ZoomBestfit();
    void LineDataPause();
    void LineDataExport();
    void LevelRange();
};

class CLineDataOptionDlg : public QDialog
{
    Q_OBJECT
public:
    explicit CLineDataOptionDlg(CLineDataCtrl& ctrl, QWidget* parent = nullptr);

private slots:
    void onOkButtonClicked();

private:
    CLineDataCtrl&      m_ctrl;
    QPushButton*        m_btn_ok;
};

class LineDataPanel : public QWidget
{
    Q_OBJECT
public:
    explicit LineDataPanel(QWidget* parent = nullptr);
    bool IsShow()const { return m_bShow; }
    void IsShow(bool val) { m_bShow = val; }

private:
    static void __stdcall LineDataCallBackV2(const unsigned* aHist, unsigned nFlag, void* ctxLineDataV2);

public slots:
    void OnLineDataOption();
    void OnZoomInX();
    void OnZoomOutX();
    void OnZoomInY();
    void OnZoomOutY();
    void OnZoomBestfit();
    void OnLineDataPause();
    void OnLineDataExport();
    void OnBufferPL16(ushort* pRaw, int width, int bitdepth);
    void OnBufferPL8(uchar* pRaw, int width, int bitdepth);

private:
    CLineDataCtrl*     m_ctrl;
    LineDataToolBar*   m_toolbar;
    bool               m_bShow;
    bool               m_bPause;
};

#endif // POLYLINEPANEL_H
