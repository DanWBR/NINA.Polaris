#import <Foundation/Foundation.h>
#import <Capacitor/Capacitor.h>

// Registers the Swift plugin + its methods with the Capacitor bridge.
CAP_PLUGIN(PolarisOnnxPlugin, "PolarisOnnx",
    CAP_PLUGIN_METHOD(createSession, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(run, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(releaseSession, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(info, CAPPluginReturnPromise);
)
