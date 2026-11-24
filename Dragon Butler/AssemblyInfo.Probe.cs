using System.Runtime.CompilerServices;

// v1.43 / 任务六：让测试探针能直接调 internal 的抓屏与解析入口。
//
// 为什么不把这些改成 public：ScreenshotMonitor.CaptureEx 和 WebhookListener 的解析细节
// 是内部实现，公开出去等于给自己加一份要长期维护的对外契约。
// 探针要验的是行为，不属于对外接口。CS8981 之类都不涉及，这里只有一行程序集特性。
[assembly: InternalsVisibleTo("CoordinatorProbe")]
