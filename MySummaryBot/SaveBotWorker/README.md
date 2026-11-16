# Save Bot integration

MySummaryBot handles Telegram updates and queues supported links for the Python worker. The worker uses the existing bot token to reply to the original message and topic. A single worker runs at a time, with room for four pending requests. Links continue to be recorded for summaries and duplicate detection.

## Enable on Fly.io

1. Add a GitHub Actions repository secret named `SAVE_BOT_READ_TOKEN` with read-only Contents access to `Theslamone/rvvrb-save-bot`. The workflow checks out the pinned commit during the build. Keep the token in GitHub Actions so it stays out of the Fly.io runtime.
2. Deploy with the existing Fly workflow. The image includes Python, FFmpeg and the Save Bot dependencies when the workflow can check out the worker. The feature starts with empty allowlists.
3. Set `SAVE_BOT_PRIVATE_CHAT_IDS` in Fly.io to a comma-separated list of approved private chat IDs. Use `/chatid` in a private conversation with the bot to find the ID. Test a video download, an AUTO.RIA gallery and an existing summary command.
4. Set `SAVE_BOT_GROUP_IDS` in Fly.io to a comma-separated list of approved group IDs when ready for the group cutover. Stop the old Save Bot service as part of the cutover to avoid duplicate replies.

An empty allowlist disables the feature for that chat type. A build without the checkout keeps the existing summary bot available. To update the packaged Save Bot, change the pinned commit in the Fly workflow and redeploy.
