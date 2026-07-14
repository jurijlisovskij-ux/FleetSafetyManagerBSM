# Fleet Safety Manager — Clean v0.14

Based directly on v0.13.

## Added: Dashboard

After login the user sees:
- Vessels
- Equipment
- OK
- Soon
- Overdue
- Fleet Status by vessel
- 20 nearest expiry dates

Access follows the existing role rules:
- Office Admin sees all vessels.
- Superintendent and Crew see assigned vessels only.

Clicking a vessel row or upcoming item opens that vessel.
Dashboard cards and buttons filter the upcoming list by status.

No database reset is required.
