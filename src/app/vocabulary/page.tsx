"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { Sidebar } from "@/components/Sidebar/Sidebar";
import { dotnetRequest } from "@/hooks/useTauriInvoke";
import { toast } from "sonner";
import { Plus, Search, Trash2, BookOpen, RefreshCw } from "lucide-react";
import { cn } from "@/lib/utils";
import type { VocabularyItem } from "@/types";

export default function VocabularyPage() {
  const router = useRouter();
  const [items, setItems] = useState<VocabularyItem[]>([]);
  const [filtered, setFiltered] = useState<VocabularyItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState({
    word: "",
    definition: "",
    translation: "",
    context: "",
  });

  useEffect(() => {
    loadVocabulary();
  }, []);

  useEffect(() => {
    const q = search.toLowerCase();
    setFiltered(
      q
        ? items.filter(
            (i) =>
              i.word.toLowerCase().includes(q) ||
              i.definition.toLowerCase().includes(q) ||
              i.spanishTranslation.toLowerCase().includes(q)
          )
        : items
    );
  }, [search, items]);

  async function loadVocabulary() {
    setLoading(true);
    try {
      await dotnetRequest("vocabulary", "initialize");
      const data = await dotnetRequest<VocabularyItem[]>("vocabulary", "list");
      setItems(Array.isArray(data) ? data : []);
    } catch {
      toast.error("No se pudo cargar el vocabulario");
    } finally {
      setLoading(false);
    }
  }

  async function handleAdd() {
    if (!form.word.trim()) {
      toast.error("La palabra es obligatoria");
      return;
    }
    try {
      await dotnetRequest("vocabulary", "add", {
        word: form.word.trim(),
        definition: form.definition.trim(),
        translation: form.translation.trim(),
        context: form.context.trim(),
      });
      toast.success(`"${form.word}" agregada`);
      setForm({ word: "", definition: "", translation: "", context: "" });
      setShowForm(false);
      await loadVocabulary();
    } catch {
      toast.error("Error al agregar la palabra");
    }
  }

  async function handleDelete(id: number, word: string) {
    try {
      await dotnetRequest("vocabulary", "delete", { id });
      setItems((prev) => prev.filter((i) => i.id !== id));
      toast.success(`"${word}" eliminada`);
    } catch {
      toast.error("Error al eliminar");
    }
  }

  return (
    <div className="flex h-screen bg-gray-50 overflow-hidden">
      <Sidebar onNewSession={() => router.push("/")} />

      <main className="flex-1 overflow-y-auto p-6">
        <div className="max-w-3xl mx-auto">
          {/* Header */}
          <div className="flex items-center justify-between mb-6">
            <div>
              <h1 className="text-xl font-semibold text-gray-900">Vocabulario</h1>
              <p className="text-sm text-gray-500 mt-0.5">{items.length} palabras registradas</p>
            </div>
            <div className="flex items-center gap-2">
              <button
                onClick={loadVocabulary}
                className="p-2 rounded-lg text-gray-400 hover:text-gray-700 hover:bg-gray-100 transition-colors"
                title="Refrescar"
              >
                <RefreshCw size={15} />
              </button>
              <button
                onClick={() => setShowForm((v) => !v)}
                className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-gray-900 text-white text-xs font-medium hover:bg-gray-700 transition-colors"
              >
                <Plus size={13} />
                Agregar palabra
              </button>
            </div>
          </div>

          {/* Add form */}
          {showForm && (
            <div className="bg-white rounded-xl border border-gray-200 p-4 mb-4 shadow-sm">
              <h2 className="text-sm font-semibold text-gray-800 mb-3">Nueva palabra</h2>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <div>
                  <label className="block text-xs text-gray-500 mb-1">Palabra *</label>
                  <input
                    className="w-full px-3 py-2 text-sm border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-gray-900"
                    placeholder="e.g. ephemeral"
                    value={form.word}
                    onChange={(e) => setForm((f) => ({ ...f, word: e.target.value }))}
                  />
                </div>
                <div>
                  <label className="block text-xs text-gray-500 mb-1">Traducción</label>
                  <input
                    className="w-full px-3 py-2 text-sm border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-gray-900"
                    placeholder="efímero"
                    value={form.translation}
                    onChange={(e) => setForm((f) => ({ ...f, translation: e.target.value }))}
                  />
                </div>
                <div className="sm:col-span-2">
                  <label className="block text-xs text-gray-500 mb-1">Definición</label>
                  <input
                    className="w-full px-3 py-2 text-sm border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-gray-900"
                    placeholder="lasting for a very short time"
                    value={form.definition}
                    onChange={(e) => setForm((f) => ({ ...f, definition: e.target.value }))}
                  />
                </div>
                <div className="sm:col-span-2">
                  <label className="block text-xs text-gray-500 mb-1">Contexto / Ejemplo</label>
                  <input
                    className="w-full px-3 py-2 text-sm border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-gray-900"
                    placeholder="The beauty of the sunset was ephemeral..."
                    value={form.context}
                    onChange={(e) => setForm((f) => ({ ...f, context: e.target.value }))}
                  />
                </div>
              </div>
              <div className="flex justify-end gap-2 mt-3">
                <button
                  onClick={() => setShowForm(false)}
                  className="px-3 py-1.5 text-xs text-gray-600 hover:text-gray-900 transition-colors"
                >
                  Cancelar
                </button>
                <button
                  onClick={handleAdd}
                  className="px-4 py-1.5 text-xs bg-gray-900 text-white rounded-lg hover:bg-gray-700 transition-colors font-medium"
                >
                  Guardar
                </button>
              </div>
            </div>
          )}

          {/* Search */}
          <div className="relative mb-4">
            <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400" />
            <input
              className="w-full pl-9 pr-3 py-2 text-sm border border-gray-200 rounded-lg bg-white focus:outline-none focus:ring-2 focus:ring-gray-900"
              placeholder="Buscar palabras…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>

          {/* Loading */}
          {loading && (
            <div className="flex items-center justify-center h-40">
              <span className="text-sm text-gray-400">Cargando vocabulario…</span>
            </div>
          )}

          {/* Empty */}
          {!loading && filtered.length === 0 && (
            <div className="flex flex-col items-center justify-center h-40 gap-2 text-gray-400">
              <BookOpen size={32} strokeWidth={1.5} />
              <p className="text-sm">
                {search ? "Sin resultados para esa búsqueda" : "Sin palabras aún. ¡Agrega la primera!"}
              </p>
            </div>
          )}

          {/* List */}
          <div className="space-y-2">
            {filtered.map((item) => (
              <div
                key={item.id}
                className="bg-white rounded-xl border border-gray-200 p-4 hover:border-gray-300 transition-all"
              >
                <div className="flex items-start justify-between gap-4">
                  <div className="flex-1 min-w-0">
                    <div className="flex items-baseline gap-2">
                      <span className="text-sm font-semibold text-gray-900">{item.word}</span>
                      {item.spanishTranslation && (
                        <span className="text-xs text-gray-500">— {item.spanishTranslation}</span>
                      )}
                      <span className={cn(
                        "ml-auto text-[10px] px-1.5 py-0.5 rounded-full font-medium",
                        item.timesEncountered > 5
                          ? "bg-green-50 text-green-600"
                          : "bg-gray-100 text-gray-500"
                      )}>
                        {item.timesEncountered}×
                      </span>
                    </div>
                    {item.definition && (
                      <p className="text-xs text-gray-500 mt-1">{item.definition}</p>
                    )}
                    {item.exampleSentence && (
                      <p className="text-xs text-gray-400 italic mt-1">"{item.exampleSentence}"</p>
                    )}
                  </div>
                  <button
                    onClick={() => handleDelete(item.id, item.word)}
                    className="p-1.5 rounded-lg text-gray-300 hover:text-red-500 hover:bg-red-50 transition-colors shrink-0"
                    title="Eliminar"
                  >
                    <Trash2 size={13} />
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      </main>
    </div>
  );
}