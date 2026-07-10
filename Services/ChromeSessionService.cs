using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace WindowsLiveCaptionsReader.Services
{
    public class ChromeSessionService
    {
        private IWebDriver? _driver;

        public bool ConnectToExistingSession()
        {
            try
            {
                var options = new ChromeOptions();
                options.DebuggerAddress = "127.0.0.1:9222";
                
                // Hide command prompt window for driver service
                var service = ChromeDriverService.CreateDefaultService();
                service.HideCommandPromptWindow = true;

                _driver = new ChromeDriver(service, options);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to connect to Chrome: {ex.Message}");
                return false;
            }
        }

        public string CaptureActiveTabContent()
        {
            if (_driver == null) return "Error: Not connected to Chrome.";

            try
            {
                // Inyectamos JS para extraer texto de manera inteligente
                // Priorizando preguntas y respuestas de Canvas/LMS
                IJavaScriptExecutor js = (IJavaScriptExecutor)_driver;
                
                string extractionScript = @"
                    function getText() {
                        // 1. Is text selected?
                        var selection = window.getSelection().toString();
                        if (selection && selection.length > 2) return 'SELECTION: ' + selection;

                        // 2. Try to find LMS specific containers (Canvas, Blackboard)
                        var querySelectors = [
                            '.question_text', 
                            '.answers', 
                            '.question-container',
                            '#questions',
                            '.assessment_content',
                            '[role=""main""]'
                        ];

                        var content = [];
                        
                        // Collect visible text from specific selectors
                        for(var sel of querySelectors) {
                            var elements = document.querySelectorAll(sel);
                            elements.forEach(el => {
                                if(el.innerText && el.innerText.trim().length > 0) {
                                    content.push(el.innerText.trim());
                                }
                            });
                        }

                        if (content.length > 0) return 'LMS-CONTENT:\n' + content.join('\n---\n');

                        // 3. Fallback: Body text but cleaned
                        return 'FULL-PAGE: ' + document.body.innerText;
                    }
                    return getText();
                ";

                var result = js.ExecuteScript(extractionScript);
                return result?.ToString() ?? "No content returned.";
            }
            catch (Exception ex)
            {
                return $"Error executing JS extraction: {ex.Message}";
            }
        }

        public void Disconnect()
        {
            try { 
                // We do NOT want to Close() or Quit() the driver as it might close the user's browser
                // Just dispose the object reference in C#
                _driver?.Dispose(); 
            } catch {}
            _driver = null;
        }
    }
}
