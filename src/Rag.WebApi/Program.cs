using Rag.Business;
using Rag.WebApi.Endpoints;
using Rag.WebApi.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------
// Three-tier composition. The web tier registers the business tier, which registers the
// data-access tier beneath it. Nothing here mentions PostgreSQL or OpenAI — that is the
// point of the split, and the reason endpoints can only reach the database through a
// business-tier service.
// ---------------------------------------------------------------------------------------

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BusinessExceptionHandler>();

builder.Services.AddBusiness(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapDocumentEndpoints();
app.MapSearchEndpoints();
app.MapHealthEndpoints();

app.Run();
