## ⚡ READ FIRST — Setup steps in order

1. **Change the live DB password** (the one shared in chat is public now):
   ```sql
   ALTER LOGIN usrsolsolerfit WITH PASSWORD = 'YourNewStrongPassword';
   ```
2. **Run `SETUP-IdentityTables.sql`** in SSMS once against your live DB.
   This creates the Identity (AspNet*) tables needed by the bridge.
   **Your m_membermaster / m_usermaster / m_statedivmaster stay untouched.**
3. **Open `appsettings.json`** and replace `__YOUR_PASSWORD__` with your new password.
4. **Visual Studio → Open solution → F5**

If login still fails after running the SQL script, check the Output window
in Visual Studio for the actual error. The DbSeeder is now wrapped in
try-catch so the app won't crash; you'll just see warnings.

---

# SolarPortal — Admin Site (Hybrid Live DB Login)

Standalone Admin deploy. Logs users in from your live solfitenergy
database via a bridge layer; everything else works as before.

## Login

Enter your **Username** (from m_usermaster.UserName) and your m_usermaster password.

When login succeeds, an Identity user is auto-created in the **same live DB**
(in the AspNet* tables) so the rest of the app — which uses Identity — keeps
working without changes.

**Your live DB tables stay untouched:**
- `m_membermaster` — read-only, never altered
- `m_usermaster` — read-only, never altered
- `m_statedivmaster` — read-only, never altered

These three tables are explicitly excluded from EF migrations via
`.ExcludeFromMigrations()` in the DbContext. EF will never generate
`ALTER` / `CREATE` / `DROP` statements for them.

## Setup steps

### 1. Change the live DB password FIRST
The password `ymEkgdfhnQsGxPeLY2CKM` was shared in chat earlier — it's
public in chat logs now. Change it on SQL Server:

```sql
ALTER LOGIN usrsolsolerfit WITH PASSWORD = 'YourNewStrongPassword';
```

### 2. Fill in the password placeholder
Open `SolarPortal.AdminWeb/appsettings.json` and replace `__YOUR_PASSWORD__` with your new
SQL Server password.

### 3. Open the solution
1. Open `SolarPortal.sln` in Visual Studio 2022
2. Set `SolarPortal.AdminWeb` as the startup project (right-click → "Set as Startup Project")
3. NuGet restore: right-click solution → "Restore NuGet Packages"

### 4. Run
Press **F5**. The site opens on `http://localhost:5039`.

On first run, EF will:
- Create the new Solar workflow tables in your live DB
  (SolarRequests, Payments, PMDocuments, etc. — ~20 tables)
- Create the Identity shadow tables (AspNetUsers, AspNetRoles, etc.)
- **NOT touch** m_membermaster, m_usermaster, or m_statedivmaster

### 5. Sign in
Enter your **Username** (from m_usermaster.UserName) and your m_usermaster password.

The first login of each user creates the shadow Identity record.
Subsequent logins compare against the current live DB value, so password
changes in m_membermaster / m_usermaster propagate automatically.

## How the bridge works

```
User types  →  AccountController.Login POST
                   ↓
                LiveDbAuthBridge.TryBridgeUserAsync(idNo, password)
                   ↓
        ┌────────────────────────────────────────┐
        │  SELECT * FROM m_membermaster          │
        │  WHERE IdNo = @idNo AND Passw = @pw    │
        └────────────────────────────────────────┘
                   ↓
            Match found → Auto-create/refresh Identity user with
                         synthetic email "member-{idNo}@livedb.local"
                   ↓
            Returns synthetic email → AccountController uses it for
                                       Identity SignInManager.PasswordSignInAsync
                   ↓
            Normal Identity sign-in completes
                   ↓
            User lands on dashboard
```

If the live DB lookup fails, the original Identity flow runs as a fallback
(useful during transition — demo accounts like `installer@solarportal.com`
still work for the Inc site).

## Common issues

**"Login network error / cannot connect"**
- Check password in appsettings.json is correct
- Verify your network can reach `103.193.74.91:15331`
- Verify the SQL user has SELECT permission on m_membermaster / m_usermaster

**"Invalid login"** but credentials are correct
- Check the user exists in the right table (m_membermaster for user site, m_usermaster for admin site)
- Verify `Passw` matches exactly (plain-text comparison, case-sensitive)
- Check the LiveDbAuthBridge log line in `Logs/solar-portal-*.txt`
