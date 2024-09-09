using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ocr.Models;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Patagames.Ocr;
using Patagames.Ocr.Enums;
using System.Drawing.Imaging;
using System.Text;
using PdfiumViewer;
using ocr.Services;
using static System.Net.Mime.MediaTypeNames;
using System.Drawing;

namespace ocr.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FilesController : ControllerBase
    {
        private readonly FilesContext _filesContext;
        private readonly VllmService _vllmService;

        public FilesController(FilesContext filesContext, VllmService vllmService)
        {
            _filesContext = filesContext;
            _vllmService = vllmService;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Files>>> GetFiles()
        {
            if (_filesContext.Files == null)
            {
                return NotFound();
            }
            return await _filesContext.Files.ToListAsync();
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Files>> GetFileById(int id)
        {
            if (_filesContext.Files is null)
            {
                return NotFound();
            }
            var file = await _filesContext.Files.FindAsync(id);
            if (file is null)
            {
                return NotFound();
            }
            return file;
        }

        [HttpPost]
        public async Task<ActionResult<Files>> PostFiles(Files file)
        {
            _filesContext.Files.Add(file);
            await _filesContext.SaveChangesAsync();
            return CreatedAtAction(nameof(GetFileById), new { id = file.id }, file);
        }

        [HttpPost("upload"), DisableRequestSizeLimit]
        public async Task<IActionResult> Upload()
        {
            try
            {
                var settings = new ElasticsearchClientSettings(new Uri("https://localhost:9200"))
                    .CertificateFingerprint("0f11565f265ab92bfaf8b71667d13474274dd57c773498566a393e8ee3ada79c")
                    .Authentication(new BasicAuthentication("elastic", "aVpEqMQ92W9h6ggk+Vgg"));

                var client = new ElasticsearchClient(settings);
                var formCollection = await Request.ReadFormAsync();
                var file = formCollection.Files.First();
                var folderName = Path.Combine("Resources", "Files");
                var pathToSave = Path.Combine(Directory.GetCurrentDirectory(), folderName);

                if (!Directory.Exists(pathToSave))
                {
                    Directory.CreateDirectory(pathToSave);
                }

                if (file.Length > 0)
                {
                    var fileName = ContentDispositionHeaderValue.Parse(file.ContentDisposition).FileName.ToString().Trim('"');
                    var fullPath = Path.Combine(pathToSave, fileName);
                    var dbPath = Path.Combine(folderName, fileName);
                    string text = string.Empty;

                    using (var stream = new FileStream(fullPath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var fileExtension = Path.GetExtension(fileName).ToLower();
                    if (fileExtension == ".pdf")
                    {
                        text = ExtractTextFromPdf(fullPath);
                    }
                    else if (fileExtension == ".jpg" || fileExtension == ".jpeg" || fileExtension == ".png" || fileExtension == ".bmp" || fileExtension == ".tiff")
                    {
                        text = ExtractTextFromImage(fullPath);
                    }
                    else
                    {
                        return BadRequest("Unsupported file type.");
                    }

                    var document = new Files
                    {
                        id = 1,
                        filename = fileName,
                        text = text,
                        fileuri = "not yet"
                    };
                    var indexResponse = await client.IndexAsync(document, (IndexName)"z");

                    if (indexResponse.IsValidResponse)
                    {
                        return Ok(new { Text = text });
                    }
                    else
                    {
                        return StatusCode(500, "Failed to index the document in Elasticsearch.");
                    }
                }
                else
                {
                    return BadRequest("No file uploaded.");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        private string ExtractTextFromImage(string imagePath)
        {
            using (var api = OcrApi.Create())
            {
                string tessdataPath = @"C:\Users\amine\OneDrive\Bureau\ocr\ocr\bin\Debug\net8.0\tessdata";
                api.Init(Languages.English, tessdataPath);

                var resizedImagePath = ResizeImageToAllowedWidth(imagePath);
                return api.GetTextFromImage(resizedImagePath);
            }
        }

        private string ResizeImageToAllowedWidth(string imagePath)
        {
            using (var image = System.Drawing.Image.FromFile(imagePath))
            {
                int allowedWidth = CalculateAllowedWidth(image.Width);
                int newHeight = (int)((double)allowedWidth / image.Width * image.Height);

                using (var resizedImage = new Bitmap(image, new Size(allowedWidth, newHeight)))
                {
                    string resizedImagePath = Path.Combine(Path.GetDirectoryName(imagePath), "resized_" + Path.GetFileName(imagePath));
                    resizedImage.Save(resizedImagePath, image.RawFormat);
                    return resizedImagePath;
                }
            }
        }

        private int CalculateAllowedWidth(int originalWidth)
        {
            if (originalWidth < 500) return originalWidth;
            return (originalWidth / 100) * 100 + 25;
        }

        private string ExtractTextFromPdf(string pdfPath)
        {
            var text = new StringBuilder();

            try
            {
                using (var pdfDocument = PdfiumViewer.PdfDocument.Load(pdfPath))
                {
                    for (int i = 0; i < pdfDocument.PageCount; i++)
                    {
                        try
                        {
                            var pageText = pdfDocument.GetPdfText(i);
                            if (!string.IsNullOrEmpty(pageText))
                            {
                                text.AppendLine(pageText);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing page {i}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading PDF: {ex.Message}");
            }

            return text.ToString();
        }

        [HttpPost("generate")]
        public async Task<IActionResult> Generate(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
            {
                return BadRequest("Prompt cannot be empty");
            }

            var settings = new ElasticsearchClientSettings(new Uri("https://localhost:9200"))
                .CertificateFingerprint("0f11565f265ab92bfaf8b71667d13474274dd57c773498566a393e8ee3ada79c")
                .Authentication(new BasicAuthentication("elastic", "aVpEqMQ92W9h6ggk+Vgg"));

            var client = new ElasticsearchClient(settings);

            var searchResponse = await client.SearchAsync<Files>(s => s
                .Index("z")
                .Query(q => q
                    .Match(m => m
                        .Field(f => f.text)
                        .Query(prompt)
                    )
                )
                .Size(1)
            );

            var document = searchResponse.Documents.FirstOrDefault();
            if (document == null)
            {
                return NotFound("No related text found in Elasticsearch.");
            }

            var relatedText = document.text;
            var newPrompt = $"Using this text from Elasticsearch: {relatedText}, answer this question: {prompt}";

            try
            {
                string result = await _vllmService.GetApiResponseAsync(newPrompt, 2000);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "An error occurred while processing your request", details = ex.Message });
            }
        }
    }
}
