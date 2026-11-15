# Microsoft WebView2 native SDK and loader

The application statically links the unmodified x64 WebView2 loader from
Microsoft.Web.WebView2 1.0.3537.50. The native SDK is fetched only by the
explicit build-time Download-WebViewSdk.ps1 helper, using the exact package
and member SHA-256 values in webview2-sdk.json. SDK headers and libraries
stay in the ignored tools/webview2-sdk cache and are not source assets.

Upstream package:
https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3537.50

LICENSE.txt and NOTICE.txt are reproduced unchanged from that package.
The loader/SDK uses the Microsoft BSD-3-Clause license. The separately
installed WebView2 Runtime has its own Microsoft distribution terms; it is
normally included in Windows 11. This SDK helper neither downloads nor
installs the Runtime, and the application has no runtime download fallback.
