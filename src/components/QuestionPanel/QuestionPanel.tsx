"use client";

import { motion, AnimatePresence } from "framer-motion";
import { cn } from "@/lib/utils";
import type { DetectedQuestion, QuestionLevel } from "@/types";
import { QUESTION_LEVEL_COLORS, QUESTION_LEVEL_LABELS } from "@/types";

interface Props {
  question: DetectedQuestion | null;
  answer: string;
  isStreaming: boolean;
}

export function QuestionPanel({ question, answer, isStreaming }: Props) {
  if (!question && !answer) return null;

  return (
    <div className="border-t border-border bg-card/50 px-3 py-2 space-y-1.5">
      <AnimatePresence mode="wait">
        {question && (
          <motion.div
            key={question.text}
            initial={{ opacity: 0, y: -4 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0 }}
            className="flex items-start gap-2"
          >
            <span
              className={cn(
                "text-xs font-mono border rounded px-1 py-0.5 shrink-0 mt-0.5",
                QUESTION_LEVEL_COLORS[question.level as QuestionLevel]
              )}
            >
              {QUESTION_LEVEL_LABELS[question.level as QuestionLevel]}
            </span>
            <p className="text-sm text-foreground font-medium">{question.text}</p>
          </motion.div>
        )}
      </AnimatePresence>

      {answer && (
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          className="text-sm text-muted-foreground leading-relaxed pl-1"
        >
          {answer}
          {isStreaming && (
            <span className="inline-block w-0.5 h-3.5 bg-primary ml-0.5 animate-cursor-blink" />
          )}
        </motion.div>
      )}
    </div>
  );
}
