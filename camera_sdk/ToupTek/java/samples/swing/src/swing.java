import java.awt.*;
import java.awt.event.*;
import java.awt.image.*;
import javax.swing.*;

public class swing {
    static toupcam _cam;
    static String _name;
    static byte[] _buf;
    static int[] _iarr;
    static BufferedImage _img;
    static int _total;
    static JFrame _frame;
    static Canvas _canvas;

    private static class ImplCanvas extends Canvas {
        @Override
        public void paint(Graphics g) {
            if (_img != null)
                g.drawImage(_img, 0, 0, getWidth(), getHeight(), 0, 0, _img.getWidth(), _img.getHeight(), null);
        }
    }

    /* run in the UI thread: AWT event dispatching thread */
    private void OnEventImage() {
        if (_cam != null) {
            try {
                _cam.PullImage(_buf, 0, 24, -1, null);
                ++_total;
            } catch (toupcam.HRESULTException ex) {
                JOptionPane.showMessageDialog(null, ex.toString());
                return;
            }
            
            WritableRaster ras = _img.getRaster();
            for (int i = 0; i < _img.getWidth() * _img.getHeight() * 3; ++i)
                _iarr[i] = _buf[i];
            ras.setPixels(0, 0, _img.getWidth(), _img.getHeight(), _iarr);
            _canvas.repaint();
            _frame.setTitle(_name + ": " + _total);
        }
    }

    private void createAndShow() {
        _frame = new JFrame("");
        _frame.setDefaultCloseOperation(JFrame.EXIT_ON_CLOSE);
        _frame.addWindowListener(new WindowAdapter() {
            @Override
            public void windowClosing(WindowEvent e) {
                if (_cam != null)
                {
                    _cam.close();
                    _cam = null;
                }
            }
        });
        _frame.getContentPane().setLayout(new BoxLayout(_frame.getContentPane(), BoxLayout.Y_AXIS));
        _frame.setSize(800, 600);

        JCheckBox checkAutoExposure = new JCheckBox("Auto Exposure");
        checkAutoExposure.setPreferredSize(new Dimension(0, 20));
        checkAutoExposure.setAlignmentX(Component.LEFT_ALIGNMENT);
        _frame.getContentPane().add(checkAutoExposure);
        checkAutoExposure.addActionListener(new ActionListener() {
            @Override
            public void actionPerformed(ActionEvent e) {
                if (_cam != null) {
                    try {
                        _cam.put_AutoExpoEnable(((JCheckBox) e.getSource()).isSelected() ? 1 : 0);
                    } catch (toupcam.HRESULTException ex) {
                        JOptionPane.showMessageDialog(null, ex.toString());
                    }
                }
            }
        });

        _canvas = new ImplCanvas();
        _frame.getContentPane().add(_canvas);

        toupcam.DeviceV2[] arr = toupcam.EnumV2();
        if (arr.length <= 0)
            _frame.setTitle("no camera found");
        else {
            _name = arr[0].displayname;
            _frame.setTitle(_name + ": 0");
            _cam = toupcam.Open(arr[0].id);
            if (_cam != null) {
                try {
                    if (0 == (arr[0].model.flag & toupcam.FLAG_MONO))
                        _cam.put_Option(toupcam.OPTION_BYTEORDER, 0);
                    checkAutoExposure.setSelected(1 == _cam.get_AutoExpoEnable());
                    int[] s = _cam.get_Size();
                    _buf = new byte[s[0] * s[1] * 3];
                    _iarr = new int[s[0] * s[1] * 3];
                    _img = new BufferedImage(s[0], s[1], BufferedImage.TYPE_INT_BGR);
                    _cam.StartPullModeWithCallback(new toupcam.IEventCallback() {
                        /* the vast majority of callbacks come from toupcam.dll/so/dylib internal threads */
                        @Override
                        public void onEvent(int nEvent) {
                            if (toupcam.EVENT_IMAGE == nEvent) {
                                SwingUtilities.invokeLater(new Runnable() {
                                    @Override
                                    public void run() {
                                        /* run in the UI thread: AWT event dispatching thread */
                                        OnEventImage();
                                    }
                                });
                            }
                        }
                    });
                } catch (toupcam.HRESULTException ex) {
                    JOptionPane.showMessageDialog(null, ex.toString());
                }
            }
        }

        checkAutoExposure.setEnabled(_cam != null);
        _frame.setVisible(true);
    }

    public static void main(String[] args) {
        javax.swing.SwingUtilities.invokeLater(new Runnable() {
            public void run() {
                swing o = new swing();
                o.createAndShow();
            }
        });
    }
}