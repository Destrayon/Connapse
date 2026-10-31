---
title: Manage users and invites
type: how-to
audience: admin
---

# Manage users and invites

Connapse is invite-only. The first account on a new server becomes its Owner; everyone after that joins through an invite link an administrator sends. This guide covers inviting people, changing their role and removing them.

## Prerequisites

- You are signed in as the Owner or an Admin.
- To invite or manage Admins, you are the Owner.

## Roles

| Role | What it can do |
|------|----------------|
| **Owner** | Everything an Admin can, plus inviting, promoting and removing Admins. There is one Owner, set when the server is first set up, and the role is never assigned afterwards. |
| **Admin** | Manage users, agents, providers, connections, sources and settings, and use MCP. |
| **Editor** | Create and delete containers, upload and delete files, and search. |
| **Viewer** | Search and browse. |

What each role and token scope allows, operation by operation, is in [Roles and scopes](../reference/roles-and-scopes.md).

## Steps

### Invite someone

1. Open **Users** in the navigation (`/admin/users`).
2. Under **Invite New User**, enter their email address and pick a role. Only the Owner sees **Admin** in the list.
3. Choose **Send Invite**, then **Copy** the invite link and send it to them yourself. Connapse doesn't email it.

The link expires in 7 days. The person opens it, sets a password, and signs in with the role you picked. Pending invites are listed under **Pending Invitations**; revoke one there to make its link stop working.

### Change someone's role

On the user's row, pick the new role and choose **Save**. Nobody changes their own role or the Owner's, and only the Owner changes an Admin's.

### Remove someone

On the user's row, choose **Delete**, then **Confirm**. The same limits apply: nobody deletes themselves or the Owner, and only the Owner deletes an Admin. Their personal access tokens stop working, and any account links they made with [custom source servers](../sources/custom-source-setup.md#account-linking) are removed.

## Do it with the API

Each call needs an Owner's or Admin's token with the `admin:users` scope (see [Tokens and agents](tokens-and-agents.md)).

Invite someone with `POST /api/v1/invites`. `role` is `Viewer`, `Editor` or `Admin`:

```bash
curl -X POST http://localhost:5001/api/v1/invites \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "email": "sam@example.com", "role": "Editor" }'
```

The answer's `token` is the link's secret: send the person `http://localhost:5001/register?token=<token>`. It is never shown again. `GET /api/v1/invites?skip=0&take=50` lists pending invites and `DELETE /api/v1/invites/{id}` revokes one.

`GET /api/v1/auth/users?skip=0&take=50` lists users. `PUT /api/v1/auth/users/{id}/roles` with `{ "roles": ["Viewer"] }` replaces a user's roles, and `DELETE /api/v1/auth/users/{id}` deletes one.

## Related

- [Tokens and agents](tokens-and-agents.md)
- [Roles and scopes](../reference/roles-and-scopes.md)
- [About permissions](../concepts/permissions.md)
