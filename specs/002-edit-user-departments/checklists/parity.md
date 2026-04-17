# Cross-Stack Parity Checklist: Edit User with Multi-Department Assignment

**Purpose**: Verify both technology stacks implement equivalent functionality for every acceptance scenario  
**Created**: 2026-03-11  
**Feature**: [spec.md](../spec.md)  

**Stacks**:
- **Stack A** — Node.js/Express + React/TypeScript ✅ Complete
- **Stack B** — .NET Blazor WASM / Clean Architecture implemented; checklist re-review pending

## US1 — Edit User Profile [Spec §US1, FR-001–FR-005, FR-013–FR-014]

- [ ] CHK001 Both stacks expose a PUT endpoint for updating user profile (full name, email, role, departments) [Parity, FR-003]
- [ ] CHK002 Both stacks display a pre-populated edit form with current full name, email, role, and departments when "Edit" is clicked [Parity, FR-002]
- [ ] CHK003 Both stacks validate that full name is not blank before accepting the form [Parity, FR-004]
- [ ] CHK004 Both stacks validate email format before accepting the form [Parity, FR-004]
- [ ] CHK005 Both stacks enforce email uniqueness and return 409 Conflict for duplicate emails [Parity, FR-005]
- [ ] CHK006 Both stacks display inline validation errors without closing or resetting the form [Parity, FR-013]
- [ ] CHK007 Both stacks update the user table immediately after a successful save without full page reload [Parity, FR-014]
- [ ] CHK008 Both stacks support cancel/close on the edit form without persisting changes [Parity, US1-AS3]

## US2 — Multi-Department Tag Input [Spec §US2, FR-006–FR-009, FR-012]

- [ ] CHK009 Both stacks render existing department assignments as individual removable tags in the edit form [Parity, FR-008]
- [ ] CHK010 Both stacks support adding new departments via tag-style input [Parity, FR-006]
- [ ] CHK011 Both stacks support removing individual department tags [Parity, US2-AS3]
- [ ] CHK012 Both stacks store departments as a comma-separated string with no spaces [Parity, FR-007]
- [ ] CHK013 Both stacks round-trip comma-separated values back to individual tags when the edit form is re-opened [Parity, US2-AS5]
- [ ] CHK014 Both stacks display department badges/tags in the user table for multi-department users [Parity, FR-007]
- [ ] CHK015 Both stacks reject department names containing commas with a clear validation message [Parity, FR-009]

## US3 — Authorization Enforcement [Spec §US3, FR-010]

- [ ] CHK016 Both stacks show the "Edit" action only to Admin users in the UI [Parity, FR-010, US3-AS1]
- [ ] CHK017 Both stacks hide the "Edit" action from non-Admin roles (Recruiter, Viewer) [Parity, US3-AS2]
- [ ] CHK018 Both stacks return 403 Forbidden when a non-Admin user calls the edit endpoint directly [Parity, FR-010, US3-AS3]

## Edge Cases [Spec §Edge Cases, FR-011–FR-012]

- [ ] CHK019 Both stacks allow admins to edit their own profile fields (name, email, departments) [Parity, Edge Case]
- [ ] CHK020 Both stacks prevent admins from changing their own role (self-role-change lock) [Parity, FR-011]
- [ ] CHK021 Both stacks allow saving a user with zero departments (empty department field) [Parity, FR-012]
- [ ] CHK022 Both stacks surface all validation errors inline without silent failures [Parity, SC-005]
- [ ] CHK023 Both stacks preserve existing single-department assignments without data loss on round-trip [Parity, SC-003]

## Notes

- Current audit result: both stacks now implement the edit-user flow, but this checklist has not yet been re-walked item by item since the Stack B changes landed.
- Stack B now includes the `PUT /api/users/{userId}` endpoint, the Blazor edit-user modal, multi-department tag editing, and focused application/web test coverage; leave checklist items unchecked until parity is re-verified against observable behavior.
- "Parity" means equivalent observable behavior; internal implementation details (e.g., EF Core vs. SQL helper) are expected to differ.
