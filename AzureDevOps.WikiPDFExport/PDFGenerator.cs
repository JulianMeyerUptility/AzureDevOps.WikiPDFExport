using System;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace azuredevops_export_wiki
{
    internal class PDFGenerator
    {
        const int MAX_PAGE_SIZE = 100_000_000;
        private ILogger _logger;
        private Options _options;

        internal PDFGenerator(Options options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        internal async Task<string> ConvertHTMLToPDFAsync(string htmlFilePath)
        {
            _logger.Log("Converting HTML to PDF");
            var output = _options.Output;
            var tempFolder = Path.Combine(Path.GetDirectoryName(output), "mermaid_diagrams");

            if (string.IsNullOrEmpty(output))
            {
                output = Path.Combine(Directory.GetCurrentDirectory(), "export.pdf");
            }

            // Ensure the temp folder exists
            Directory.CreateDirectory(tempFolder);

            var chromePath = _options.ChromeExecutablePath;

            if (string.IsNullOrEmpty(chromePath))
            {
                string tempFolderPath = Path.Join(Path.GetTempPath(), "AzureDevOpsWikiExporter");

                _logger.Log("No Chrome path defined, downloading to user temp...");

                var fetcherOptions = new BrowserFetcherOptions
                {
                    Path = tempFolderPath,
                };

                // Create an instance of BrowserFetcher and download the latest version
                var browserFetcher = new BrowserFetcher(fetcherOptions);
                var revisionInfo = await browserFetcher.DownloadAsync(); // Automatically downloads the latest revision
                chromePath = browserFetcher.GetExecutablePath(revisionInfo.BuildId);

                _logger.Log("Chrome ready.");
            }

            var launchOptions = new LaunchOptions
            {
                ExecutablePath = chromePath,
                Headless = true, // Change to false for debugging
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu" }, // Add additional flags if needed
                Timeout = 120000 // Increase timeout to 120 seconds (2 minutes)
            };

            using (var browser = await Puppeteer.LaunchAsync(launchOptions))
            {
                var page = await browser.NewPageAsync();

                _logger.Log($"Sending file to Chrome: {htmlFilePath}");
                
                // Read HTML content from file
                string htmlContent = await File.ReadAllTextAsync(htmlFilePath);

                _logger.Log($"HTML content read from {htmlFilePath}");

                await page.SetContentAsync(htmlContent, new NavigationOptions { Timeout = 60000 }); // Set to 60 seconds
                _logger.Log($"HTML page loaded.");

                if (_options.RenderMermaidAsImages)
                {
                    // Inject Mermaid.js
                    await page.EvaluateExpressionAsync($@"
                        const script = document.createElement('script');
                        script.type = 'text/javascript';
                        script.src = 'file:///{_options.MermaidJsPath.Replace("\\", "/")}';
                        document.head.appendChild(script);
                    ");

                    // Wait for the script to load
                    await Task.Delay(1000); // Adjust the time as needed

                    // Initialize Mermaid.js
                    await page.EvaluateExpressionAsync("mermaid.initialize({ startOnLoad: true });");

                    // Ensure diagrams have time to render
                    await Task.Delay(1000); // Adjust the time as needed

                    // Verify Mermaid.js initialization
                    var isInitialized = await page.EvaluateExpressionAsync<bool>("typeof mermaid !== 'undefined' && mermaid.init !== undefined");

                    if (!isInitialized)
                    {
                        throw new Exception("Mermaid.js failed to initialize.");
                    }

                    // Access rendered diagrams
                    var elements = await page.QuerySelectorAllAsync(".mermaid");

                    // Example: Capturing screenshots of Mermaid diagrams
                    _logger.Log("Capturing Mermaid diagrams as images...");
                    var screenshotPaths = new List<string>();

                    for (int i = 0; i < elements.Length; i++)
                    {
                        var element = elements[i];
                        var screenshotPath = Path.Combine(tempFolder, $"mermaid_{i}.png");
                        await element.ScreenshotAsync(screenshotPath);
                        screenshotPaths.Add(screenshotPath);
                        _logger.Log($"Captured screenshot: {screenshotPath}");
                    }

                    // // Inject captured images into the HTML
                    // foreach (var screenshotPath in screenshotPaths)
                    // {
                    //     htmlContent += $"<div><img src='file://{screenshotPath.Replace("\\", "/")}' /></div>";
                    // }

                    // // Save modified HTML to file
                    // htmlFilePath = Path.Combine(tempFolder, "output_with_images.html");
                    // await File.WriteAllTextAsync(htmlFilePath, htmlContent);
                    // _logger.Log($"Modified HTML content saved to {htmlFilePath}");
                }

                // await page.GoToAsync($"file://{htmlFilePath}", launchOptions.Timeout);
                // _logger.Log($"Modified HTML page loaded.");

                // Generate PDF
                var pdfOptions = new PdfOptions
                {
                    PrintBackground = true,
                    PreferCSSPageSize = false,
                    DisplayHeaderFooter = true,
                    MarginOptions = {
                        Top = "80px",
                        Bottom = "100px",
                        Left = "100px",
                        Right = "100px"
                    },
                    Format = PuppeteerSharp.Media.PaperFormat.A4
                };

                if (!string.IsNullOrEmpty(_options.HeaderTemplate))
                {
                    pdfOptions.HeaderTemplate = _options.HeaderTemplate;
                }
                else if (!string.IsNullOrEmpty(_options.HeaderTemplatePath))
                {
                    pdfOptions.HeaderTemplate = File.ReadAllText(_options.HeaderTemplatePath);
                }

                if (!string.IsNullOrEmpty(_options.FooterTemplate))
                {
                    pdfOptions.FooterTemplate = _options.FooterTemplate;
                }
                else if (!string.IsNullOrEmpty(_options.FooterTemplatePath))
                {
                    pdfOptions.FooterTemplate = File.ReadAllText(_options.FooterTemplatePath);
                }

                _logger.Log($"Generating PDF document...");
                await page.PdfAsync(output, pdfOptions);
                await browser.CloseAsync();
                _logger.Log($"PDF document is ready.");
            }

            _logger.Log($"PDF created at: {output}");
            return output;
        }
    }
}