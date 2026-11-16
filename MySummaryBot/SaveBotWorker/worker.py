"""Handle one link-bearing Telegram message from MySummaryBot."""

import asyncio
import json
import os
import sys
from pathlib import Path

from aiogram import Bot, Dispatcher
from aiogram.types import Message, Update

sys.path.insert(0, str(Path(__file__).resolve().parent / "source"))

from app.config import Settings
from app.main import make_router


async def handle_message(bot: Bot, message: Message) -> None:
    chat_ids = frozenset({message.chat.id})
    settings = Settings(
        token=bot.token,
        allowed_chat_ids=chat_ids,
        allowed_private_chat_ids=chat_ids,
        max_concurrent_jobs=1,
        download_timeout=90,
    )
    dispatcher = Dispatcher()
    dispatcher.include_router(make_router(settings))
    await dispatcher.feed_update(bot, Update(update_id=0, message=message))


async def main() -> None:
    message = Message.model_validate(json.load(sys.stdin))
    async with Bot(os.environ.pop("TTOKEN")) as bot:
        await handle_message(bot, message)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:
        print(f"Save Bot worker failed ({type(error).__name__})", file=sys.stderr)
        raise SystemExit(1) from None
