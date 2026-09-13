---
type: "query"
date: "2026-08-25T13:49:09.121729+00:00"
question: "Can daemonized Magnetar processes run at high priority when Quasar has a lower CPU priority in Docker?"
contributor: "graphify"
outcome: "useful"
source_nodes: ["DedicatedServerSupervisor", ".StartProcessAsync()", ".TryApplyProcessPriority()", ".TryApplyUnixNice()", "DedicatedServerProcessPriority"]
---

# Q: Can daemonized Magnetar processes run at high priority when Quasar has a lower CPU priority in Docker?

## Answer

Magnetar -daemon normally detaches in place with setsid, preserving its PID and nice state. DedicatedServerSupervisor applies the configured startup priority to that same process after launch. The Compose manifest grants SYS_NICE, allowing Docker Engine to raise Magnetar above Quasar's nice level. Container-wide CPU shares, quotas, cpus, and cpuset controls apply to the whole cgroup and cannot be overridden; the manifest and Docker guide warn users not to add them. Rootless runtimes that cannot grant effective SYS_NICE must not lower Quasar priority.

## Outcome

- Signal: useful

## Source Nodes

- DedicatedServerSupervisor
- .StartProcessAsync()
- .TryApplyProcessPriority()
- .TryApplyUnixNice()
- DedicatedServerProcessPriority