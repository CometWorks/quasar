using System.Runtime.CompilerServices;
using Quasar.Services;

// Preserve the type identity used by UI plugins compiled against the worker.
[assembly: TypeForwardedTo(typeof(ViewerTimeZone))]
