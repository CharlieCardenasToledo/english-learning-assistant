"use client";

import { listen } from "@tauri-apps/api/event";
import { useEffect } from "react";
import type { TranscriptionLine, TranslationLine, DetectedQuestion } from "@/types";

interface CaptionHandlers {
  onTranscriptionLine?:    (payload: TranscriptionLine) => void;
  onTranscriptionPartial?: (payload: { text: string; timestamp: string }) => void;
  onTranslationReady?:     (payload: TranslationLine) => void;
  onQuestionDetected?:     (payload: DetectedQuestion) => void;
  onAnswerChunk?:          (payload: { chunk: string }) => void;
  onAnswerComplete?:       (payload: { answer: string }) => void;
  onStatusChanged?:        (payload: { status: string }) => void;
}

const isTauri = () =>
  typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

export function useCaptionEvents(handlers: CaptionHandlers) {
  useEffect(() => {
    if (!isTauri()) return;

    const subscriptions = [
      handlers.onTranscriptionLine &&
        listen<TranscriptionLine>("transcription-line", (e) =>
          handlers.onTranscriptionLine!(e.payload)
        ),
      handlers.onTranscriptionPartial &&
        listen<{ text: string; timestamp: string }>("transcription-partial", (e) =>
          handlers.onTranscriptionPartial!(e.payload)
        ),
      handlers.onTranslationReady &&
        listen<TranslationLine>("translation-ready", (e) =>
          handlers.onTranslationReady!(e.payload)
        ),
      handlers.onQuestionDetected &&
        listen<DetectedQuestion>("question-detected", (e) =>
          handlers.onQuestionDetected!(e.payload)
        ),
      handlers.onAnswerChunk &&
        listen<{ chunk: string }>("answer-chunk", (e) =>
          handlers.onAnswerChunk!(e.payload)
        ),
      handlers.onAnswerComplete &&
        listen<{ answer: string }>("answer-complete", (e) =>
          handlers.onAnswerComplete!(e.payload)
        ),
      handlers.onStatusChanged &&
        listen<{ status: string }>("status-changed", (e) =>
          handlers.onStatusChanged!(e.payload)
        ),
    ].filter(Boolean) as Promise<() => void>[];

    return () => {
      subscriptions.forEach((p) => p.then((fn) => fn()));
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}
