# Antigravity Usage Tracker

Tray tool & floating Mini HUD theo dõi token usage, quota và chi phí cho **Google Antigravity (AGY)** trên Windows.

## Tính năng chính

- **System Tray & Mini HUD**: Hiển thị mức sử dụng hiện tại ngay trên màn hình, hỗ trợ kéo thả vị trí, điều chỉnh độ mờ, tự ẩn/snap theo cửa sổ ứng dụng.
- **Theo dõi Transcript thời gian thực**: Tự động quét và lắng nghe thay đổi từ `~/.gemini/antigravity/brain` (và CLI brain root) để thống kê token, burn rate, cache hit %, model đang dùng.
- **Đồng bộ Quota `agy`**: Tích hợp `AgyQuotaService` theo dõi giới hạn Gemini 5h, Gemini Weekly và Argon Weekly kèm thời gian reset.
- **Thống kê chi tiết (`StatsWindow`)**: Biểu đồ theo giờ (24h), lọc theo khoảng thời gian (Today, 7d, 30d, All), phân tích theo model, ước tính chi phí, top tools, lịch sử theo ngày và danh sách lỗi gần đây.
- **Cảnh báo & Webhook**: Cảnh báo ngưỡng usage (5h / 7d / context / Argon 7d) và hỗ trợ gửi thông báo qua webhook.
- **Global Hotkeys & Compact**: Phím tắt toàn cục và hỗ trợ compact session nhanh.
- **Song ngữ EN / VI**: Chuyển đổi linh hoạt giữa Tiếng Việt và Tiếng Anh.

## Yêu cầu hệ thống

- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (nếu build từ mã nguồn; bản Release đã đóng gói self-contained)

## Build & Chạy thử

```powershell
# Build ứng dụng chính
dotnet build AntigravityUsageTracker.csproj

# Chạy ứng dụng
dotnet run --project AntigravityUsageTracker.csproj

# Chạy bộ kiểm thử SelfTest (126 test cases)
dotnet run --project SelfTest/SelfTest.csproj
```

## Cấu trúc thư mục

- `Models/` — Data models cho usage records, summary, config và quota
- `Services/` — Quét transcript, watcher, quota sync, thống kê, cảnh báo, webhook, i18n
- `Views/` — Giao diện WPF (`MiniHudWindow`, `StatsWindow`, `SettingsWindow`, `CompactToastWindow`)
- `SelfTest/` — Bộ unit/integration tests độc lập
- `.github/workflows/` — CI kiểm thử tự động và Release đóng gói `win-x64`

## License

[MIT](LICENSE)
