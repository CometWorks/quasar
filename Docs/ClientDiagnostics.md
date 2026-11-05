# Cluster client diagnostics

The Cluster Registry publishes the allowlisted snapshot and stores the Cluster Manager's
rotation assignments. Cluster nodes deliver it over secure game mod messages; players
never need Quasar, Registry credentials or local lifetime files. Enable the matching
Pulsar Cluster Diagnostics client, then open F11. In-game Admin and Owner ranks receive
fleet/World Authority/Registry detail. Moderator and Space Master use player mode.

In **Settings → Advanced cluster controls → Client diagnostics and rotation publication**,
load the current publication to inspect its source, revision and validity. A manager can
publish a replacement using the JSON editor. Quasar requires cluster-manage permission
and the Registry's `PUT /admin/v1/rotation` capability; the read requires cluster-query.
The existing per-cluster service-principal restrictions and durable operation store apply.

The publication contains `expectedRevision`, `source`, `validUntil`, `enabled`, and a
`nodes` array of `{node, epoch, dueAt}`. It replaces the full fleet list. Each assignment
names an exact serving regular-node incarnation. Validity is at most five minutes;
renew every 30 seconds. Disabled rotation has no assignments. Expired, drained, restarted
or spare nodes lose their countdown. Publishing never executes a rotation, and due time
is not a promise that a drain has started.

Automation uses GET/PUT `/api/v1/clusters/{cluster}/rotation`, command action
`rotation-set`, or `Quasar cluster rotation-set CLUSTER publication.json
--idempotency-key KEY`. Read with `Quasar cluster rotation CLUSTER`. The matching
snapshot read is `/api/v1/clusters/{cluster}/client-diagnostics?node=ID` or
`Quasar cluster client-diagnostics CLUSTER NODE`. The Cluster Manager can publish its
existing assigned-lifetime file through the cluster package's `nodes publish-rotation`
command; files and local publisher PIDs stay on the Manager host.

Upgrade the cluster first to a release advertising `rotationPublication: v1` and
`clientDiagnostics: v1`, then deploy matching client code. Older Registries return an
unavailable capability/404; unrelated Quasar functionality remains usable. This change
adds contracts and communication, not a replacement rotation scheduler.
