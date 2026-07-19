#import <Foundation/Foundation.h>
#import <Capacitor/Capacitor.h>

// Register the plugin + its methods with the Capacitor bridge (Obj-C macros).
CAP_PLUGIN(PolarisLlamaPlugin, "PolarisLlama",
    CAP_PLUGIN_METHOD(downloadModel, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(deleteModel, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(start, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(stop, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(status, CAPPluginReturnPromise);
)
