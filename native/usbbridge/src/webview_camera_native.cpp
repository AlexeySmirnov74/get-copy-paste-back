// Isolated, local-only WebView2 host. No USB ownership or driver changes.
#include <windows.h>
#include <commdlg.h>
#include <dwmapi.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>
#include <algorithm>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>
using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
namespace {
constexpr wchar_t origin[]=L"https://camera.usbbridge.invalid";
constexpr wchar_t site_origin[]=L"https://website-camera.usbbridge.invalid";
constexpr wchar_t site_url[]=L"https://website-camera.usbbridge.invalid/index.html";
constexpr wchar_t site_window_class[]=L"USBBridge.WebViewCamera.Website.1";
constexpr wchar_t page_url[]=L"https://camera.usbbridge.invalid/index.html";
constexpr wchar_t client_window_class[]=L"USBBridge.WebViewCamera.Client.1";
constexpr wchar_t site_client_window_class[]=L"USBBridge.WebViewCamera.Website.Client.1";
constexpr wchar_t window_class[]=L"USBBridge.WebViewCamera.Server.1";
constexpr size_t max_message_units=262144;
using Sink=void(__cdecl*)(void*,HWND,const wchar_t*);
struct Failure{HRESULT code;};
void checked(HRESULT code){if(FAILED(code))throw Failure{code};}
struct Text {
 LPWSTR value=nullptr;
 ~Text(){CoTaskMemFree(value);}
};
struct Host:std::enable_shared_from_this<Host> {
 HWND window=nullptr;
 HWND error_label=nullptr;
 bool closed=false;
 bool configured=false;
 bool site_check=false;
 bool permission_granted=false;
 bool permission_pending=false;
 const wchar_t* url()const{return site_check?site_url:page_url;}
 const wchar_t* web_origin()const{return site_check?site_origin:origin;}
 bool trusted(const wchar_t* value)const{return value&&wcscmp(value,url())==0;}
 bool trusted_permission(const wchar_t* value)const {
  return value&&(trusted(value)||wcscmp(value,web_origin())==0||std::wstring(value)==std::wstring(web_origin())+L"/");
 }
 HRESULT failure=S_OK;
 std::string html;
 std::wstring bootstrap;
 std::wstring user_data;
 std::wstring title;
 std::wstring startup_error;
 Sink sink=nullptr;
 void* sink_context=nullptr;
 unsigned check_seconds=0;
 ComPtr<ICoreWebView2Environment> environment;
 ComPtr<ICoreWebView2Controller> controller;
 ComPtr<ICoreWebView2> view;
 HBRUSH background=CreateSolidBrush(RGB(15,23,32));
 ~Host(){close();DeleteObject(background);}
 void close(){
  if(closed)return;
  closed=true;
  if(controller)controller->Close();
  view.Reset();controller.Reset();environment.Reset();
 }
 void fail(HRESULT result){
  if(closed||FAILED(failure))return;
  failure=result;
  KillTimer(window,1);
  wchar_t code[32]{};swprintf_s(code,L"\nHRESULT: 0x%08X",static_cast<unsigned>(result));
  auto text=startup_error+code;
  if(view)view->Stop();
  if(controller)controller->Close();
  view.Reset();controller.Reset();
  error_label=CreateWindowExW(0,L"STATIC",text.c_str(),WS_CHILD|WS_VISIBLE|SS_LEFT,
     24,28,900,180,window,nullptr,GetModuleHandleW(nullptr),nullptr);
  SendMessageW(error_label,WM_SETFONT,reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)),TRUE);
  if(sink){
   wchar_t message[100]{};swprintf_s(message,L"{\"type\":\"host_error\",\"hresult\":%ld}",static_cast<long>(result));
   sink(sink_context,window,message);
  }
 }
 void resize(){
  if(closed)return;
  RECT bounds{};GetClientRect(window,&bounds);
  if(controller){controller->put_Bounds(bounds);controller->NotifyParentWindowPositionChanged();}
  if(error_label)MoveWindow(error_label,24,28,std::max(120L,bounds.right-48),std::max(80L,bounds.bottom-56),TRUE);
 }
 void configure(){
  if(configured||closed||FAILED(failure)||!view)return;
  configured=true;
  auto weak=weak_from_this();
  const HRESULT result=view->ExecuteScript(bootstrap.c_str(),Callback<ICoreWebView2ExecuteScriptCompletedHandler>(
   [weak](HRESULT code,LPCWSTR)->HRESULT{if(auto self=weak.lock();self&&!self->closed&&FAILED(code))self->fail(code);return S_OK;}).Get());
  if(FAILED(result))fail(result);
 }
 void navigate(){
  if(closed||FAILED(failure)||!view)return;
  const HRESULT result=view->Navigate(url());
  if(FAILED(result))fail(result);
 }
 void initialize_view(ICoreWebView2Controller* created){
  checked(created?S_OK:E_POINTER);
  controller=created;checked(controller->get_CoreWebView2(&view));
  ComPtr<ICoreWebView2Settings> settings;checked(view->get_Settings(&settings));
  checked(settings->put_IsScriptEnabled(TRUE));
  checked(settings->put_IsWebMessageEnabled(TRUE));
  checked(settings->put_AreHostObjectsAllowed(FALSE));
  checked(settings->put_AreDefaultScriptDialogsEnabled(FALSE));
  checked(settings->put_AreDefaultContextMenusEnabled(FALSE));
  checked(settings->put_AreDevToolsEnabled(FALSE));
  checked(settings->put_IsStatusBarEnabled(FALSE));
  ComPtr<ICoreWebView2Settings3> settings3;
  if(SUCCEEDED(settings.As(&settings3)))settings3->put_AreBrowserAcceleratorKeysEnabled(FALSE);
  ComPtr<ICoreWebView2Controller2> controller2;
  if(SUCCEEDED(controller.As(&controller2)))controller2->put_DefaultBackgroundColor({255,15,23,32});
  auto weak=weak_from_this();EventRegistrationToken token{};
  checked(view->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
   [weak](ICoreWebView2*,ICoreWebView2NavigationStartingEventArgs* args)->HRESULT{
    Text uri;args->get_Uri(&uri.value);
    const auto self=weak.lock();args->put_Cancel(!self||self->closed||!self->trusted(uri.value));
    return S_OK;
   }).Get(),&token));
  checked(view->add_FrameNavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
   [](ICoreWebView2*,ICoreWebView2NavigationStartingEventArgs* args)->HRESULT{args->put_Cancel(TRUE);return S_OK;}).Get(),&token));
  checked(view->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
   [](ICoreWebView2*,ICoreWebView2NewWindowRequestedEventArgs* args)->HRESULT{args->put_Handled(TRUE);return S_OK;}).Get(),&token));
  checked(view->add_PermissionRequested(Callback<ICoreWebView2PermissionRequestedEventHandler>(
   [weak](ICoreWebView2*,ICoreWebView2PermissionRequestedEventArgs* args)->HRESULT{
    Text uri;COREWEBVIEW2_PERMISSION_KIND kind{};args->get_Uri(&uri.value);args->get_PermissionKind(&kind);
    const auto self=weak.lock();
    const bool camera=self&&!self->closed&&SUCCEEDED(self->failure)&&self->trusted_permission(uri.value)&&(!self->site_check||self->permission_granted)&&kind==COREWEBVIEW2_PERMISSION_KIND_CAMERA;
    args->put_State(camera?COREWEBVIEW2_PERMISSION_STATE_ALLOW:COREWEBVIEW2_PERMISSION_STATE_DENY);
    ComPtr<ICoreWebView2PermissionRequestedEventArgs3> args3;
    if(SUCCEEDED(args->QueryInterface(IID_PPV_ARGS(&args3))))args3->put_SavesInProfile(FALSE);
    return S_OK;
   }).Get(),&token));
  checked(view->AddWebResourceRequestedFilter(L"*",COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL));
  checked(view->add_WebResourceRequested(Callback<ICoreWebView2WebResourceRequestedEventHandler>(
   [weak](ICoreWebView2*,ICoreWebView2WebResourceRequestedEventArgs* args)->HRESULT{
    const auto self=weak.lock();if(!self||self->closed||FAILED(self->failure))return E_ABORT;
    ComPtr<ICoreWebView2WebResourceRequest> request;Text uri;
    if(FAILED(args->get_Request(&request))||FAILED(request->get_Uri(&uri.value)))return E_FAIL;
    const bool allowed=self->trusted(uri.value);
    ComPtr<IStream> body;HRESULT result=CreateStreamOnHGlobal(nullptr,TRUE,&body);
    if(FAILED(result))return result;
    if(allowed){
     ULONG written=0;result=body->Write(self->html.data(),static_cast<ULONG>(self->html.size()),&written);
     if(FAILED(result)||written!=self->html.size())return E_FAIL;
     LARGE_INTEGER zero{};result=body->Seek(zero,STREAM_SEEK_SET,nullptr);if(FAILED(result))return result;
    }
    ComPtr<ICoreWebView2WebResourceResponse> response;
    result=self->environment->CreateWebResourceResponse(body.Get(),allowed?200:403,allowed?L"OK":L"Forbidden",
     L"Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n"
     L"Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; "
     L"connect-src 'none'; media-src blob:; img-src data:; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n"
     L"Permissions-Policy: camera=(self), microphone=(self), geolocation=()\r\n",&response);
    if(SUCCEEDED(result))result=args->put_Response(response.Get());
    return result;
   }).Get(),&token));
  checked(view->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
   [weak](ICoreWebView2*,ICoreWebView2WebMessageReceivedEventArgs* args)->HRESULT{
    const auto self=weak.lock();if(!self||self->closed||FAILED(self->failure))return S_OK;
    Text uri;Text message;
    if(FAILED(args->get_Source(&uri.value))||!self->trusted(uri.value)||FAILED(args->get_WebMessageAsJson(&message.value)))return S_OK;
    if(!message.value||wcsnlen_s(message.value,max_message_units+1)>max_message_units)return S_OK;
    if(self->sink)self->sink(self->sink_context,self->window,message.value);
    return S_OK;
   }).Get(),&token));
  checked(view->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>(
   [weak](ICoreWebView2*,ICoreWebView2NavigationCompletedEventArgs* args)->HRESULT{
    if(auto self=weak.lock();self&&!self->closed){
     BOOL success=FALSE;args->get_IsSuccess(&success);
     if(success){KillTimer(self->window,1);self->configure();}
     else self->fail(E_FAIL);
    }
    return S_OK;
   }).Get(),&token));
  ComPtr<ICoreWebView2_4> view4;checked(view.As(&view4));
  checked(view4->add_DownloadStarting(Callback<ICoreWebView2DownloadStartingEventHandler>(
   [](ICoreWebView2*,ICoreWebView2DownloadStartingEventArgs* args)->HRESULT{args->put_Cancel(TRUE);return S_OK;}).Get(),&token));
  resize();checked(controller->put_IsVisible(TRUE));
  // The diagnostic grants CAMERA without opening a default device. The separate
  // website check starts at DEFAULT every time, including after a prior grant.
  ComPtr<ICoreWebView2_13> view13;checked(view.As(&view13));
  ComPtr<ICoreWebView2Profile> profile;checked(view13->get_Profile(&profile));
  ComPtr<ICoreWebView2Profile4> profile4;checked(profile.As(&profile4));
  checked(profile4->SetPermissionState(COREWEBVIEW2_PERMISSION_KIND_CAMERA,web_origin(),
   site_check?COREWEBVIEW2_PERMISSION_STATE_DEFAULT:COREWEBVIEW2_PERMISSION_STATE_ALLOW,Callback<ICoreWebView2SetPermissionStateCompletedHandler>(
    [weak](HRESULT code)->HRESULT{
     if(auto self=weak.lock();self&&!self->closed){if(SUCCEEDED(code))self->navigate();else self->fail(code);}
     return S_OK;
    }).Get()));
 }
 void initialize(){
  auto weak=weak_from_this();
  auto options=Microsoft::WRL::Make<CoreWebView2EnvironmentOptions>();
  checked(options->put_AdditionalBrowserArguments(L"--disable-background-networking --disable-component-update --no-first-run"));
  checked(options->put_AllowSingleSignOnUsingOSPrimaryAccount(FALSE));
  checked(CreateCoreWebView2EnvironmentWithOptions(nullptr,user_data.c_str(),options.Get(),
   Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
    [weak](HRESULT result,ICoreWebView2Environment* created)->HRESULT{
     auto self=weak.lock();if(!self||self->closed||FAILED(self->failure))return S_OK;
     if(FAILED(result)||!created){self->fail(FAILED(result)?result:E_POINTER);return S_OK;}
     self->environment=created;
     result=created->CreateCoreWebView2Controller(self->window,
      Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
       [weak](HRESULT code,ICoreWebView2Controller* controller)->HRESULT{
        auto target=weak.lock();if(!target||target->closed||FAILED(target->failure)){if(controller)controller->Close();return S_OK;}
        try{checked(code);target->initialize_view(controller);}
        catch(const Failure& error){target->fail(error.code);}catch(...){target->fail(E_FAIL);}
        return S_OK;
       }).Get());
     if(FAILED(result))self->fail(result);
     return S_OK;
    }).Get()));
 }
};
LRESULT CALLBACK procedure(HWND window,UINT message,WPARAM wp,LPARAM lp){
 auto* owner=reinterpret_cast<std::shared_ptr<Host>*>(GetWindowLongPtrW(window,GWLP_USERDATA));
 if(message==WM_NCCREATE){owner=static_cast<std::shared_ptr<Host>*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(owner));}
 const auto self=owner?*owner:std::shared_ptr<Host>{};
 if(self){
  switch(message){
   case WM_SIZE:case WM_MOVE:self->resize();return 0;
   case WM_GETMINMAXINFO:{auto* limits=reinterpret_cast<MINMAXINFO*>(lp);limits->ptMinTrackSize={820,600};return 0;}
   case WM_TIMER:if(wp==1)self->fail(HRESULT_FROM_WIN32(ERROR_TIMEOUT));else if(wp==2)PostMessageW(window,WM_CLOSE,0,0);return 0;
   case WM_CTLCOLORSTATIC:SetTextColor(reinterpret_cast<HDC>(wp),RGB(225,233,242));SetBkColor(reinterpret_cast<HDC>(wp),RGB(15,23,32));return reinterpret_cast<LRESULT>(self->background);
   case WM_ERASEBKGND:{RECT area{};GetClientRect(window,&area);FillRect(reinterpret_cast<HDC>(wp),&area,self->background);return 1;}
   case WM_CLOSE:self->close();DestroyWindow(window);return 0;
   case WM_DESTROY:self->close();PostQuitMessage(0);return 0;
  }
 }
 return DefWindowProcW(window,message,wp,lp);
}
} // namespace
extern "C" int ub_webview_camera_run(const char* html,size_t html_length,const wchar_t* script,
 const wchar_t* user_data,const wchar_t* title,const wchar_t* error_text,void* context,Sink sink,unsigned check_seconds,int site_check,int client_role){
 const HRESULT apartment=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
 if(FAILED(apartment))return apartment;
 HRESULT result=S_OK;
 try{
  Text version;checked(GetAvailableCoreWebView2BrowserVersionString(nullptr,&version.value));
  auto host=std::make_shared<Host>();host->html.assign(html,html_length);host->bootstrap=script;
  host->user_data=user_data;host->title=title;host->startup_error=error_text;host->sink=sink;host->sink_context=context;host->check_seconds=check_seconds;host->site_check=site_check!=0;
  const auto host_class=client_role?(host->site_check?site_client_window_class:client_window_class):(host->site_check?site_window_class:window_class);
  WNDCLASSEXW cls{};cls.cbSize=sizeof(cls);cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=host_class;
  cls.lpfnWndProc=procedure;cls.hCursor=LoadCursorW(nullptr,MAKEINTRESOURCEW(32512));cls.hbrBackground=host->background;
  cls.hIcon=LoadIconW(cls.hInstance,MAKEINTRESOURCEW(101));cls.hIconSm=cls.hIcon;
  if(!RegisterClassExW(&cls)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)throw Failure{HRESULT_FROM_WIN32(GetLastError())};
  host->window=CreateWindowExW(0,host_class,title,WS_OVERLAPPEDWINDOW, CW_USEDEFAULT,CW_USEDEFAULT,1100,820,nullptr,nullptr,cls.hInstance,&host);
  if(!host->window)throw Failure{HRESULT_FROM_WIN32(GetLastError())};
  const BOOL dark=TRUE;DwmSetWindowAttribute(host->window,20,&dark,sizeof(dark));
  ShowWindow(host->window,SW_SHOWNORMAL);UpdateWindow(host->window);
  SetTimer(host->window,1,25000,nullptr);
  if(check_seconds)SetTimer(host->window,2,check_seconds*1000,nullptr);
  try{host->initialize();}catch(const Failure& error){host->fail(error.code);}
  MSG message{};while(GetMessageW(&message,nullptr,0,0)>0){TranslateMessage(&message);DispatchMessageW(&message);}
  result=host->failure;host->close();UnregisterClassW(host_class,cls.hInstance);
 }catch(const Failure& error){result=error.code;}catch(...){result=E_FAIL;}
 CoUninitialize();return result;
}
extern "C" int ub_webview_camera_copy(HWND owner,const wchar_t* text){
 if(!text)return E_POINTER;
 const size_t length=wcsnlen_s(text,max_message_units+1);if(length>max_message_units)return E_INVALIDARG;
 const size_t bytes=(length+1)*sizeof(wchar_t);
 HGLOBAL memory=GlobalAlloc(GMEM_MOVEABLE,bytes);if(!memory)return E_OUTOFMEMORY;
 void* data=GlobalLock(memory);if(!data){GlobalFree(memory);return E_OUTOFMEMORY;}
 memcpy(data,text,bytes);GlobalUnlock(memory);
 if(!OpenClipboard(owner)){GlobalFree(memory);return HRESULT_FROM_WIN32(GetLastError());}
 EmptyClipboard();const bool success=SetClipboardData(CF_UNICODETEXT,memory)!=nullptr;CloseClipboard();
 if(!success)GlobalFree(memory);
 return success?S_OK:E_FAIL;
}
extern "C" void ub_webview_camera_ack(HWND window,const wchar_t* json){
 auto* holder=reinterpret_cast<std::shared_ptr<Host>*>(GetWindowLongPtrW(window,GWLP_USERDATA));
 if(holder&&*holder&&!(*holder)->closed&&(*holder)->view)(*holder)->view->PostWebMessageAsJson(json);
}
extern "C" int ub_webview_camera_save_path(HWND owner,const wchar_t* title,wchar_t* path,unsigned capacity){
 if(!path||capacity<64)return E_INVALIDARG;
 wcscpy_s(path,capacity,L"webview-camera-report.json");
 OPENFILENAMEW dialog{};dialog.lStructSize=sizeof(dialog);dialog.hwndOwner=owner;dialog.lpstrTitle=title;
 dialog.lpstrFilter=L"JSON (*.json)\0*.json\0\0";dialog.lpstrFile=path;dialog.nMaxFile=capacity;dialog.lpstrDefExt=L"json";
 dialog.Flags=OFN_OVERWRITEPROMPT|OFN_PATHMUSTEXIST|OFN_NOCHANGEDIR;
 if(GetSaveFileNameW(&dialog))return S_OK;
 const DWORD error=CommDlgExtendedError();return error?HRESULT_FROM_WIN32(error):S_FALSE;
}

extern "C" void ub_webview_camera_permission(HWND window,int allow){
 auto* holder=reinterpret_cast<std::shared_ptr<Host>*>(GetWindowLongPtrW(window,GWLP_USERDATA));
 const auto self=holder?*holder:std::shared_ptr<Host>{};
 if(!self||self->closed||!self->view||!self->site_check||self->permission_pending)return;
 self->permission_pending=true;
 auto fail=[self](){self->permission_pending=false;self->permission_granted=false;
  if(!self->closed&&self->view)self->view->PostWebMessageAsJson(L"{\"type\":\"permission-result\",\"status\":\"error\"}");};
 ComPtr<ICoreWebView2_13> view13;ComPtr<ICoreWebView2Profile> profile;ComPtr<ICoreWebView2Profile4> profile4;
 if(FAILED(self->view.As(&view13))||FAILED(view13->get_Profile(&profile))||FAILED(profile.As(&profile4))){fail();return;}
 const auto weak=self->weak_from_this();
 const HRESULT result=profile4->SetPermissionState(COREWEBVIEW2_PERMISSION_KIND_CAMERA,self->web_origin(),
  allow?COREWEBVIEW2_PERMISSION_STATE_ALLOW:COREWEBVIEW2_PERMISSION_STATE_DENY,
  Callback<ICoreWebView2SetPermissionStateCompletedHandler>([weak,allow](HRESULT code)->HRESULT{
   if(auto target=weak.lock();target&&!target->closed&&target->view){
    target->permission_pending=false;target->permission_granted=SUCCEEDED(code)&&allow;
    target->view->PostWebMessageAsJson(FAILED(code)?L"{\"type\":\"permission-result\",\"status\":\"error\"}":
     allow?L"{\"type\":\"permission-result\",\"status\":\"granted\"}":L"{\"type\":\"permission-result\",\"status\":\"denied\"}");
   }return S_OK;
  }).Get());
 if(FAILED(result))fail();
}
