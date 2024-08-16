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

namespace ocr.Controllers

{
    [Route("api/[controller]")]
    [ApiController]
    public class FilesController : ControllerBase

    { 
        private readonly FilesContext _filesContext;
        public FilesController(FilesContext filesContext)
        {
            _filesContext = filesContext;
        }
        // Get : api/Files
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Files>>> GetFiles()
        {
            if (_filesContext.Files == null)
            {
                return NotFound();
            }
            return await _filesContext.Files.ToListAsync();
        }
        // Get : api/Files/2

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
        // Post : api/files
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

                    // Determine file type by extension
                    var fileExtension = Path.GetExtension(fileName).ToLower();
                    if (fileExtension == ".pdf")
                    {
                        // Process PDF: Convert each page to an image and extract text
                        text = ExtractTextFromPdf(fullPath);
                    }
                    else if (fileExtension == ".jpg" || fileExtension == ".jpeg" || fileExtension == ".png" || fileExtension == ".bmp" || fileExtension == ".tiff")
                    {
                        // Process Image: Extract text directly from image
                        text = ExtractTextFromImage(fullPath);
                    }
                    else
                    {
                        return BadRequest("Unsupported file type.");
                    }

                    // Index the extracted text into Elasticsearch
                    var document = new Files
                    {
                        id = 1,
                        filename = fileName,
                        text = text,
                        fileuri = "not yet"
                    };
                    var indexResponse = await client.IndexAsync(document, (IndexName) "z");

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
                // Log the exception here for security purposes, instead of exposing it directly
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        private string ExtractTextFromImage(string imagePath)
        {
            using (var api = OcrApi.Create())
            {
                string tessdataPath = @"C:\Users\amine\OneDrive\Bureau\ocr\ocr\bin\Debug\net8.0\tessdata";
                api.Init(Languages.English, tessdataPath);
                return api.GetTextFromImage(imagePath);
            }
        }

        private string ExtractTextFromPdf(string pdfPath)
        {
            var text = new StringBuilder();

            // Use PdfiumViewer to open and render the PDF
            using (var pdfDocument = PdfiumViewer.PdfDocument.Load(pdfPath))
            {
                for (int i = 0; i < pdfDocument.PageCount; i++)
                {
                    // Render each page to an image
                    using (var img = pdfDocument.Render(i, 500, 550, PdfRenderFlags.CorrectFromDpi))
                    {
                        // Save the rendered image to a temporary file
                        var pageImagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
                        img.Save(pageImagePath, ImageFormat.Png);

                        // Extract text from the image
                        text.Append(ExtractTextFromImage(pageImagePath));

                        // Clean up the temporary file
                        System.IO.File.Delete(pageImagePath);
                    }
                }
            }

            return text.ToString();
        }
        






    }
}
