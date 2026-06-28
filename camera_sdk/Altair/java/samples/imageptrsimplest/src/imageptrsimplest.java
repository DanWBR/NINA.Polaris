import com.sun.jna.Structure;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.Arrays;

public class imageptrsimplest {
    static altaircam _cam = null;
    static int _total = 0;

    private static class ImplEventCallback implements altaircam.IEventCallback {
        /* the vast majority of callbacks come from altaircam.dll/so/dylib internal threads */
        @Override
        public void onEvent(int nEvent) {
            switch (nEvent) {
                case altaircam.EVENT_IMAGE:
                    try {
                        altaircam.ImagePtr ptrImage = new altaircam.ImagePtr();
                        _cam.PullImagePtr(0, ptrImage);
                        ++_total;
                        altaircam.FrameInfoV4 s = altaircam.Ptr2FrameInfoV4(ptrImage.ptrInfo);
                        System.out.printf("pull image ok: %d, width = %d, height = %d\n", _total, s.v3.width, s.v3.height);
                        _cam.PushImagePtr(ptrImage.id);
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
                }
            }
        }
    }
}
