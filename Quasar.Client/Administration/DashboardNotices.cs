using Quasar.Services.Updates;

namespace Quasar.Client.Administration;

public sealed record DashboardNotices(UpdateNotice[] Notices, bool TokenExpired, string? BrandingRevision = null);
