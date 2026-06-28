import java.io.IOException;

public class simplest {
    static altaircam _cam = null;
    static byte[] _buf = null;
    static int _total = 0;

    private static class ImplEventCallback implements altaircam.IEventCallback {
        /* the vast majority of callbacks come from altaircam.dll/so/dylib internal threads */
        @Override
        public void onEvent(int nEvent) {
            switch (nEvent) {
                case altaircam.EVENT_IMAGE:
                    try {
                        altaircam.FrameInfoV4 info = new altaircam.FrameInfoV4();
                        _cam.PullImage(_buf, 0, 24, -1, info);
                        ++_total;
                        System.out.printf("pull image ok: %d, %02x\n", _total, _buf[_buf.length / 2]);
                    } catch (altaircam.HRESULTException e) {
                        System.out.println("pull image exception: " + e);
                    }
                    break;
                default:
                    System.out.println("event callback: " + nEvent);
                    break;
            }
        }
    }
    
    public static void main(String[] args) {
        altaircam.DeviceV2[] arr = altaircam.EnumV2();
        if (arr.length == 0)
            System.out.println("no camera found");
        else {
            System.out.println(arr[0].displayname + ": 0x" + Long.toHexString(arr[0].model.flag) + ", preview = " + arr[0].model.preview + ", still = " + arr[0].model.still);
            for (int i = 0; i < arr[0].model.res.length; ++i)
                System.out.println(arr[0].model.res[i].width + " x " + arr[0].model.res[i].height);

            _cam = altaircam.Open(arr[0].id);
            if (_cam != null) {
                try {
                    int[] s = _cam.get_Size();
                    int bufsize = altaircam.TDIBWIDTHBYTES(s[0] * 24) * s[1];
                    System.out.printf("width = %d, height = %d, bufsize = %d\n", s[0], s[1], bufsize);
                    _buf = new byte[bufsize];
                    _cam.StartPullModeWithCallback(new ImplEventCallback());
                    System.out.println("Press Enter to exit");
                    try {
                        System.in.read();
                    } catch (IOException e) {
                    }
                } catch (altaircam.HRESULTException e) {
                    System.out.println("start camera exception: " + e);
                } finally {
                    _cam.close();
                    _cam = null;
                    _buf = null;
                }
            }
        }
    }
}
