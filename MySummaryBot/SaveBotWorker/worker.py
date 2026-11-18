"""Handle one link-bearing Telegram message from MySummaryBot."""

import asyncio
import json
import os
import sys
from pathlib import Path

import aiohttp
from aiogram import Bot, Dispatcher
from aiogram.client.session.middlewares.base import BaseRequestMiddleware
from aiogram.methods import (
    EditMessageCaption,
    EditMessageText,
    SendAnimation,
    SendAudio,
    SendDocument,
    SendMediaGroup,
    SendMessage,
    SendPhoto,
    SendVideo,
)
from aiogram.types import Message, Update

sys.path.insert(0, str(Path(__file__).resolve().parent / "source"))

from app.config import Settings
from app.main import make_router

TRANSLATE_PROMPT = (
    "Translate the user's text into Ukrainian. If it is already Ukrainian, return it unchanged. "
    "Keep HTML tags, URLs, @mentions, #hashtags, emoji and line breaks exactly as they are. "
    "Return only the resulting text."
)
CAPTION_METHODS = (SendPhoto, SendVideo, SendAnimation, SendDocument, SendAudio, EditMessageCaption)
TEXT_METHODS = (SendMessage, EditMessageText)


async def translate(text: str) -> str:
    if not any(ch.isalpha() for ch in text):
        return text
    try:
        async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=30)) as http:
            async with http.post(
                "https://api.openai.com/v1/chat/completions",
                headers={"Authorization": f"Bearer {os.environ['OPENAI_API_KEY']}"},
                json={
                    "model": "gpt-4.1-mini",
                    "temperature": 0,
                    "messages": [
                        {"role": "system", "content": TRANSLATE_PROMPT},
                        {"role": "user", "content": text},
                    ],
                },
            ) as response:
                response.raise_for_status()
                result = (await response.json())["choices"][0]["message"]["content"].strip()
                return result or text
    except Exception as error:
        print(f"Translation failed ({type(error).__name__})", file=sys.stderr)
        return text


class TranslateMiddleware(BaseRequestMiddleware):
    async def __call__(self, make_request, bot, method):
        # Translated text no longer matches entity offsets, so entities are dropped.
        if isinstance(method, CAPTION_METHODS) and method.caption:
            method = method.model_copy(update={
                "caption": await translate(method.caption), "caption_entities": None})
        elif isinstance(method, TEXT_METHODS) and method.text:
            method = method.model_copy(update={"text": await translate(method.text), "entities": None})
        elif isinstance(method, SendMediaGroup):
            media = [
                item.model_copy(update={"caption": await translate(item.caption), "caption_entities": None})
                if item.caption else item
                for item in method.media
            ]
            method = method.model_copy(update={"media": media})
        return await make_request(bot, method)


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
        bot.session.middleware(TranslateMiddleware())
        await handle_message(bot, message)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:
        print(f"Save Bot worker failed ({type(error).__name__})", file=sys.stderr)
        raise SystemExit(1) from None
