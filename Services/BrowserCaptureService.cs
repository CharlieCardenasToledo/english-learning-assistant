using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation; // Requires UIAutomationClient and UIAutomationTypes assemblies

namespace WindowsLiveCaptionsReader.Services
{
    public class BrowserCaptureService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        public async Task<string> GetSelectedTextAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 1. Get the current active window
                    IntPtr handle = GetForegroundWindow();
                    if (handle == IntPtr.Zero) return "No active window detected.";

                    // Check if it's a browser (optimization)
                    GetWindowThreadProcessId(handle, out int processId);
                    var process = Process.GetProcessById(processId);
                    string procName = process.ProcessName.ToLower();

                    // Only support major browsers for now
                    if (!procName.Contains("chrome") && !procName.Contains("msedge") && !procName.Contains("firefox"))
                    {
                        // Fallback: Try anyway, but warn log?
                        // return string.Empty; 
                    }

                    // 2. Get Automation Element
                    AutomationElement element = AutomationElement.FromHandle(handle);
                    if (element == null) return "Could not access window UI.";

                    // 3. Find the focused element (where selection likely is)
                    AutomationElement? focusedElement = null;
                    try
                    {
                        focusedElement = AutomationElement.FocusedElement;
                    }
                    catch 
                    {
                        // Fallback validation
                    }

                    // ... (previous selection logic) ...
                    
                    if (focusedElement != null)
                    {
                        // Try TextPattern selection
                        if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
                        {
                            var textPattern = (TextPattern)patternObj;
                            var selection = textPattern.GetSelection();
                            if (selection.Length > 0) return selection[0].GetText(-1).Trim();
                        }
                        
                        // Try ValuePattern
                        if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePatternObj))
                        {
                             var valuePattern = (ValuePattern)valuePatternObj;
                             string val = valuePattern.Current.Value;
                             if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }

                    // --- FALLBACK: DEEP SCAN ---
                    // If no selection, try to find the main "Document" element and read it.
                    // We start from the focused element (which is likely inside the document) and walk up 
                    // or start from window root and find the Document control type.
                    
                    AutomationElement? documentElement = null;
                    
                    // Strategy A: Walk up from focus
                    if (focusedElement != null)
                    {
                        var walker = TreeWalker.ControlViewWalker;
                        var parent = walker.GetParent(focusedElement);
                        while (parent != null)
                        {
                            if (parent.Current.ControlType == ControlType.Document || parent.Current.ControlType == ControlType.Pane)
                            {
                                // Check if this pane/doc has significant text
                                if (parent.TryGetCurrentPattern(TextPattern.Pattern, out object txtPat))
                                {
                                    documentElement = parent;
                                    break;
                                }
                            }
                            parent = walker.GetParent(parent);
                        }
                    }

                    // Strategy B: Find Document from Window Root using raw search
                    if (documentElement == null)
                    {
                         // Relaxed condition: Find ANY pane that might contain web content
                         var condition = new OrCondition(
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)
                         );
                         var potentialDocs = element.FindAll(TreeScope.Children, condition); // Only children first to avoid deep recursion
                         
                         foreach(AutomationElement potential in potentialDocs)
                         {
                             // Heuristic: If it has a Name, likely the tab content title
                             if (!string.IsNullOrEmpty(potential.Current.Name))
                             {
                                 documentElement = potential;
                                 break;
                             }
                         }
                    }

                    // Strategy C: LAST RESORT - AGGRESSIVE WINDOW SCAN
                    if (documentElement == null)
                    {
                        // Just scan the whole window root, but limit depth strictly
                        StringBuilder catchAll = new StringBuilder();
                        WalkTree(element, catchAll, 0);
                        if (catchAll.Length > 0) return "FULL-WINDOW-SCAN: " + catchAll.ToString();
                    }

                    if (documentElement != null)
                    {
                        // 1. Try generic TextPattern
                        if (documentElement.TryGetCurrentPattern(TextPattern.Pattern, out object docPat))
                        {
                             string fullText = ((TextPattern)docPat).DocumentRange.GetText(5000); 
                             if (!string.IsNullOrWhiteSpace(fullText)) return "AUTO-SCAN-DOC: " + fullText;
                        }
                        
                        // 2. Manual Tree Walk
                        StringBuilder contentBuilder = new StringBuilder();
                        WalkTree(documentElement, contentBuilder, 0);
                        if (contentBuilder.Length > 0) return "DEEP-SCAN-DOC: " + contentBuilder.ToString();
                    }
                    
                    // Diagnostic return (temporary)
                    return $"Debug: Window found ({procName}), but no content accessible. Focus was: {(focusedElement?.Current.ControlType.ProgrammaticName ?? "Null")}";
                }
                catch (Exception ex)
                {
                    return $"Error capturing text: {ex.Message}";
                }
            });
        }

        private void WalkTree(AutomationElement root, StringBuilder sb, int depth)
        {
            if (depth > 8 || sb.Length > 5000) return; // Increased limits for deeper scan

            try 
            {
                var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement child in children)
                {
                    string extracted = "";

                    // Priority 1: ValuePattern (Inputs, Read-only fields)
                    if (child.TryGetCurrentPattern(ValuePattern.Pattern, out object valPat))
                    {
                        extracted = ((ValuePattern)valPat).Current.Value;
                    }

                    // Priority 2: Name Property (Labels, Links, Headings)
                    if (string.IsNullOrWhiteSpace(extracted))
                    {
                        extracted = child.Current.Name;
                    }
                    
                    // Priority 3: Legacy Accessible Value (Old reliable for web)
                    if (string.IsNullOrWhiteSpace(extracted))
                    {
                         // Using object pattern directly if interface not available in early binding
                         // But we can check property directly
                         try {
                             object patternObj;
                             if (child.TryGetCurrentPattern(AuthenticationLegacyPattern(), out patternObj))
                             {
                                 // Reflection or dynamic if types missing, but usually LegacyIAccessiblePattern is standard in .NET 4.7.1+
                                 // Since we might be on older framework or incomplete wrapper, let's skip complex casting if not easy.
                                 // ACTUALLY: In .NET Core/5+, LegacyIAccessiblePattern is available.
                             }
                         } catch {} 
                    }

                    // Filtering Noise
                    if (!string.IsNullOrWhiteSpace(extracted) && extracted.Length > 3) 
                    {
                        // Clean up common noise
                        if (extracted != "Chrome Legacy Window" && !extracted.Contains("http") && extracted.Length < 500)
                        {
                             sb.AppendLine(extracted);
                        }
                    }

                    // Recurse for container types
                    ControlType ct = child.Current.ControlType;
                    if (ct == ControlType.Group || ct == ControlType.Pane || ct == ControlType.Document || 
                        ct == ControlType.List || ct == ControlType.ListItem || ct == ControlType.Custom)
                    {
                         WalkTree(child, sb, depth + 1);
                    }
                }
            } catch {}
        }
        
        // Helper to get Legacy Pattern safe
        private AutomationPattern AuthenticationLegacyPattern()
        {
            // Dynamically resolve if needed, or return standard
            return AutomationPattern.LookupById(10018); // LegacyIAccessiblePatternId
        }
        
        // Helper to check if a process is a browser
        public bool IsBrowserActive()
        {
            try
            {
                IntPtr handle = GetForegroundWindow();
                if (handle == IntPtr.Zero) return false;
                GetWindowThreadProcessId(handle, out int processId);
                var process = Process.GetProcessById(processId);
                string n = process.ProcessName.ToLower();
                return n.Contains("chrome") || n.Contains("msedge") || n.Contains("firefox") || n.Contains("opera") || n.Contains("brave");
            }
            catch { return false; }
        }
    }
}
