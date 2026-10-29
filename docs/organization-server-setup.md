# Organization server setup — P26 Railway-first candidate

Open **My Research → Organizations → Create an Organization Server**. Railway is the primary **Deployment Candidate**. **Review Railway Candidate** explains costs, storage and limitations. No live **Deploy with Railway** action is offered: an official native template, verified ingress and fresh-account novice deployment remain unaccepted. Personal Research needs no server or provider account.

The owner-approved target has five stages: **Create Organization Server → Deploy with Railway and authenticate → Review actual charges → Secure ownership, MFA and recovery → Invite pilots**. It is a goal, not a completed acceptance claim. Each organization pays its own provider directly. No central AeroScience account or developer-funded hosting is required.

## Secure an already provisioned candidate

A legitimate hosting-account owner must obtain the protected one-time setup code from the provider's private deployment settings. Paste it into **Setup code**, name the organization, and choose a strong administrator password. This private-dashboard action has not been eliminated by the Railway candidate; it is an explicitly remaining ownership-delivery step, not a hidden infrastructure prerequisite. Do not put the code in a link or public message. The managed lifetime is persisted, is 5–60 minutes, and does not reset on restart. Successful initialization consumes the code atomically. An anonymous first visitor cannot become administrator.

After signing in, production administrators are directed to **Secure Your Organization**:

1. **Set Up Authenticator** using your current password. **Copy Setup Key** into your authenticator app; the key is masked and never placed in a URL.
2. Enter its current code and choose **Enable Authenticator**.
3. **Save Recovery Codes** somewhere separate from this server. Enter **One saved recovery code**, then **Confirm Saved Recovery**. That code is genuinely tested and consumed; nine remain.

Password alone cannot administer a production server after this change. Subsequent **Verify Your Sign In** accepts a fresh authenticator code or an unused saved recovery code. Using recovery revokes earlier sessions and desktop grants. **Account Security** supports deliberate replacement of an authenticator/recovery codes after recent second-factor verification and password confirmation. Replacing codes invalidates the old set. Passkeys, email recovery and unattended recovery are not implemented.

## Organization Setup

**Organization Setup** brings together the saved name/address, **Account Security**, **Server Protection**, **Start Research** and **Invite Pilots**. These are sections within the hosting/security/invitation journey, not additional infrastructure setup stages. **Create First Program** / **Open Research Programs** and **Create Campaign** reuse the existing publication workflow.

**Invite Pilots → Members → Create Invitation → Create Invitation Link** produces an expiring, single-use link. **Copy Invitation Link** and share it yourself; no email-server configuration is needed. Pilots connect from **My Research → Organizations** using the existing browser-approved matching-code permissions. Joining never shares private flight archives.

## Server Health and Server Protection

**Server Health → Check Server Health** checks real bounded schema, TLS, received-evidence storage, protected identity, active-administrator MFA and recovery-point metadata. **Production Ready: No** remains visible. A successful internal health probe establishes application/database availability, not a verified public deployment, recoverability or scientific acceptance.

**Server Protection** shows actual logical database/evidence usage and the most recent recovery operations. Without a protected destination it shows **Recovery storage is not connected** and offers no fake backup action. Only an active administrator with verified MFA can inspect or operate protection.

When recovery is configured by a supported operator/template, **Backup Schedule → Save Backup Settings** requires explicit organizational-backup consent and a password. **Back Up Now** queues a consistent encrypted PostgreSQL/identity snapshot. **Verify Restore** restores into an isolated disposable database and verifies original evidence/audit identities and encrypted MFA. It does not replace the live organization. **Download Recovery Key** is an explicit sensitive download: keep it separately from the server/account, and retain independently accessible encrypted backup storage as well.

**Delete expired verified recovery points under this daily and weekly policy** is off by default. It becomes available only with a separate recovery destination, and requires your explicit backup-policy approval. AeroScience restores the separately encrypted copy before retention may delete expired verified points. It protects the newest verified checkpoint, recent and unverified points; deleted points stay labeled in history. Independent account control, live loss recovery and guided disaster promotion remain gates. **Prepare Verified Update** checks a configured signed pinned distribution and prepares a paired recovery checkpoint. It does not deploy an image, migrate production or claim rollback. Ordinary production startup still refuses pending migrations. A protected operator-only runner can apply exact signed compiled transactional schema changes after the verified preflight; provider rollout and beginner update/recovery acceptance remain incomplete.

## Privacy and acceptance

Backups contain organization accounts, audit/review records, identity material and only evidence already expressly received through P17. They cannot access a pilot's private computer, unsent queue or scientific source library. Deleting local evidence before delivery still holds/cancels the queued upload. After authorized receipt the organization owns a separate disclosed copy; local deletion does not delete that remote copy or its backups.

No owner developer QA or paid-account action is requested for the automated implementation checkpoint. Live paid hosting and novice recovery require a separate explicitly authorized acceptance stage. See [provider/cost research](organization-hosting-providers.md), [security/recovery reference](architecture/organization-recovery.md), [Railway candidate](../deploy/organization/railway/README.md) and [advanced self-hosting](../deploy/organization/self-hosted/README.md).
