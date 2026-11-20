#import <Foundation/Foundation.h>
#import <Capacitor/Capacitor.h>

// Registers the Swift plugin and its methods with the Capacitor bridge.
CAP_PLUGIN(PolarisSmbPlugin, "PolarisSmb",
    CAP_PLUGIN_METHOD(discover, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(hasCredentials, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(forgetCredentials, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(listShares, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(list, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(download, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(cancelDownload, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(releaseFile, CAPPluginReturnPromise);
    CAP_PLUGIN_METHOD(describeUri, CAPPluginReturnPromise);
)
