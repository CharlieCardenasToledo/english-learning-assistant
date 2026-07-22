export interface TranscriptionLine {
  text: string;
  timestamp: string;
  sequenceId: number;
}

export interface TranslationLine {
  original: string;
  translated: string;
  provider: string;
  fromCache: boolean;
}

export interface DetectedQuestion {
  text: string;
  level: 1 | 2 | 3 | 4;
  confidence: number;
}

export interface QuestionConversationItem extends DetectedQuestion {
  id: string;
  answer: string;
  status: "queued" | "processing" | "answered" | "failed";
}

export interface AnswerChunk {
  chunk: string;
}

export interface Session {
  id: number;
  title?: string;
  startTime: string;
  endTime?: string;
  transcriptionCount: number;
  questionCount: number;
}


export interface SessionEntry {
  id: number;
  originalText: string;
  translatedText?: string;
  timestamp: string;
  containsQuestion?: boolean;
  aiResponse?: string;
}

export interface SessionQuestion {
  id: number;
  questionText: string;
  suggestedAnswer?: string;
  wasAnswered: boolean;
  detectedAt: string;
}

export interface SessionDetail extends Session {
  title?: string;
  summary?: string;
  status?: string;
  duration?: string;
  entries: SessionEntry[];
  questions: SessionQuestion[];
}
export interface VocabularyItem {
  id: number;
  word: string;
  definition: string;
  spanishTranslation: string;
  exampleSentence: string;
  timesEncountered: number;
  firstSeen: string;
  lastSeen: string;
}

export type QuestionLevel = 1 | 2 | 3 | 4;

export const QUESTION_LEVEL_COLORS: Record<QuestionLevel, string> = {
  1: "text-green-400 border-green-400",
  2: "text-yellow-400 border-yellow-400",
  3: "text-orange-400 border-orange-400",
  4: "text-red-400 border-red-400",
};

export const QUESTION_LEVEL_LABELS: Record<QuestionLevel, string> = {
  1: "L1 · Explícita",
  2: "L2 · WH-word",
  3: "L3 · Indirecta",
  4: "L4 · IA",
};