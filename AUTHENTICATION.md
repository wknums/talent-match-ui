# Authentication System

## Overview

The Talent Matching Platform now includes a simple username/password authentication system with role-based access control. This is designed for initial demos with the understanding that it will be replaced with Entra ID or B2B authentication in production.

## Default Admin Account

On first launch, the system creates a default admin account:
- **Username**: `admin`
- **Password**: `adm1n99`

**Important**: Change this password immediately after first login.

## User Roles

### Admin
- **Access**: Can view and manage all jobs across all departments
- **Capabilities**:
  - Create, edit, and manage all jobs
  - Create new user accounts
  - Reset passwords for any user
  - Delete users (except themselves)
  - Approve or reject password reset requests from recruiters
  - View pending password reset requests

### Recruiter
- **Access**: Can only view jobs from their assigned department
- **Capabilities**:
  - Create and edit jobs within their department
  - Upload and review applications for their jobs
  - Perform manual reviews
  - Change their own password
  - Request password reset from admin

## User Management (Admin Only)

Admins can access the User Management interface from the user menu:

1. **Create Users**:
   - Click "Add User" button
   - Fill in username, full name, email (optional), department (optional), and role
   - Set initial password (minimum 6 characters)
   - User can change password after first login

2. **Reset Passwords**:
   - Click "Reset Password" next to any user
   - Enter new password (minimum 6 characters)
   - Password is immediately updated

3. **Delete Users**:
   - Click delete icon next to any user
   - Confirm deletion
   - Note: Cannot delete your own admin account

4. **Password Reset Requests**:
   - Pending requests appear at the top of the User Management dialog
   - Shows requester name, username, and request timestamp
   - Approve: Enter new password for the user
   - Reject: Dismiss the request

## Self-Service Password Management

All users can change their own password:

1. Click on your user menu (top right)
2. Select "Change Password"
3. Enter current password
4. Enter and confirm new password (minimum 6 characters)
5. Submit

## Password Reset Request (Recruiters)

If a recruiter forgets their password:

1. Click on user menu
2. Select "Request Password Reset"
3. Admin will be notified of the request
4. Admin can approve and set a new password
5. User will be able to log in with the new password

## Data Storage

All authentication data is stored using the Spark KV persistence API:
- Passwords are hashed using SHA-256
- User data persists between sessions
- Password reset requests are tracked until resolved

## Security Notes

- Minimum password length: 6 characters
- Passwords are hashed before storage
- Old password required for self-service password changes
- Admin role required for user management operations
- Users cannot delete themselves
- Session persists in browser storage

## Future Migration to Entra ID

This authentication system is designed to be easily replaced with Entra ID or B2B authentication. Key integration points:

1. Replace `login()` function with Entra ID authentication
2. Map Entra ID roles to application roles
3. Replace `getCurrentUser()` with Entra ID user info
4. Remove password management features (handled by Entra ID)
5. Update department filtering to use Entra ID group membership

## Example Usage

### Creating a Recruiter for Engineering Department

As admin:
1. Open User Management
2. Click "Add User"
3. Enter:
   - Username: `john.doe`
   - Full Name: `John Doe`
   - Email: `john.doe@company.com`
   - Department: `Engineering`
   - Role: `Recruiter`
   - Password: `temp123`
4. John can now log in and will only see Engineering jobs

### Approving a Password Reset

As admin:
1. Open User Management
2. See pending request from user
3. Click "Approve"
4. Enter new password in prompt
5. User can now log in with new password
