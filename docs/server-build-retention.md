# Server build and disk policy

Keep the current verified release and one working rollback for each application.
Bantera's protected releases come from bantera-current.json and its previous-release.json, plus live tunnel origins and retained container mounts. Never clean a release before successful public verification. Older releases retain only compact audit records and database backups.

Use /home/ubuntu/maintenance/cleanup-builds.py --apply after successful releases. It removes obsolete Bantera containers/images/source copies; across all applications it removes images not referenced by ANY container and older than 72 hours, and disposable builder cache (retain up to 2 GiB of reusable cache). Untagged, unused image layers are removed after deployment. Recent in-progress Bantera release directories and labeled images are protected. Unknown containers, all volumes, databases, uploads, model caches, checkout source and backups are protected. Do not use docker volume prune or docker system prune --volumes.

Before a build, require free space >= max(20 GiB, 10% of disk capacity), with additional allowance for unusually large builds. If below threshold, clean known disposable artifacts first, then refuse the build if still below threshold.

The hourly disk guard triggers safe cleanup at 80% used or below 30 GiB free. Below 20 GiB or above 90% after cleanup, it fails loudly in the system journal for operator attention. This service does not send external email or notifications and cannot guarantee capacity against unbounded uploads or other persistent data growth.

Every new Docker application and connector container must specify json-file logging with max-size=10m and max-file=3. Compose deployments should set equivalent logging options. Do not restart the Docker daemon or recreate active applications simply to change historical log settings. The host journal is capped at 512 MiB with 14-day retention.

Maintenance commands:
- python3 /home/ubuntu/maintenance/cleanup-builds.py (dry-run report)
- python3 /home/ubuntu/maintenance/cleanup-builds.py --apply
- systemctl status server-disk-guard.timer
- journalctl -u server-disk-guard.service

Never overlap build cleanup with a deployment. Bantera controllers and cleanup share /home/ubuntu/releases/bantera-deploy.lock. Other applications' release procedures must also coordinate maintenance or hold this lock during builds.
