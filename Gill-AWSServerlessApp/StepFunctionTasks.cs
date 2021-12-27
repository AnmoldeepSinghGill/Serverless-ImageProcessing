using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Gill_AWSServerlessApp.Models;
using Gill_AWSServerlessApp.Operations;

/*
 * Created By:
 * Name: Anmoldeep Singh Gill
 * Student Number: 301044883
 */

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Gill_AWSServerlessApp
{
    public class StepFunctionTasks
    {
        /// <summary>
        /// The default minimum confidence used for detecting labels.
        /// </summary>
        public const float DEFAULT_MIN_CONFIDENCE = 90f;

        /// <summary>
        /// The name of the environment variable to set which will override the default minimum confidence level.
        /// </summary>
        public const string MIN_CONFIDENCE_ENVIRONMENT_VARIABLE_NAME = "MinConfidence";

        IAmazonS3 S3Client { get; }

        IAmazonRekognition RekognitionClient { get; }

        IDynamoDBContext DynamoDbContext { get; }

        float MinConfidence { get; set; } = DEFAULT_MIN_CONFIDENCE;

        HashSet<string> ImageTypesSupported { get; } = new HashSet<string> { ".png", ".jpg", ".jpeg" };

        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        public StepFunctionTasks()
        {
            // create new S3 client, rekognition client and dynamo db context instances
            this.S3Client = new AmazonS3Client();
            this.RekognitionClient = new AmazonRekognitionClient();
            AmazonDynamoDBClient AWSDynamoClient = new AmazonDynamoDBClient();
            this.DynamoDbContext = new DynamoDBContext(AWSDynamoClient);

            var environmentMinConfidence = System.Environment.GetEnvironmentVariable(MIN_CONFIDENCE_ENVIRONMENT_VARIABLE_NAME);
            if (!string.IsNullOrWhiteSpace(environmentMinConfidence))
            {
                float value;
                if (float.TryParse(environmentMinConfidence, out value))
                {
                    this.MinConfidence = value;
                    Console.WriteLine($"Setting minimum confidence to {this.MinConfidence}");
                }
                else
                {
                    Console.WriteLine($"Failed to parse value {environmentMinConfidence} for minimum confidence. Reverting back to default of {this.MinConfidence}");
                }
            }
            else
            {
                Console.WriteLine($"Using default minimum confidence of {this.MinConfidence}");
            }
        }

        /// <summary>
        /// This method is called for every DetectImageLabelsDynamoDB Task Lambda invocation.
        /// This lambda function is called if the image uploaded to /images folder is
        /// an image file and recogonizes the labels from image and save them in dynamo db table
        /// </summary>
        /// <param name="input"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task<string> DetectImageLabelsDynamoDB(S3Event input, ILambdaContext context)
        {
            try
            {
                foreach (var record in input.Records)
                {
                    // hceck if image is of type .jpg, .png or .jpeg
                    if (!ImageTypesSupported.Contains(Path.GetExtension(record.S3.Object.Key)))
                    {
                        Console.WriteLine($"Object {record.S3.Bucket.Name}:{record.S3.Object.Key} is not a supported image type");
                        continue;
                    }

                    // detect labels from the image
                    Console.WriteLine($"Looking for labels in image {record.S3.Bucket.Name}:{record.S3.Object.Key}");
                    var detectResponses = await this.RekognitionClient.DetectLabelsAsync(new DetectLabelsRequest
                    {
                        MinConfidence = MinConfidence,
                        Image = new Image
                        {
                            S3Object = new Amazon.Rekognition.Model.S3Object
                            {
                                Bucket = record.S3.Bucket.Name,
                                Name = record.S3.Object.Key
                            }
                        }
                    });

                    // add the labels to the image label list
                    List<ImageLabel> imageLabels = new List<ImageLabel>();
                    foreach (var label in detectResponses.Labels)
                    {
                        // just a precaution if minConfidence environment variable is overriden this
                        // will prevent any labels below 90 to be added into dynamo db
                        if (label.Confidence > 90f)
                        {
                            Console.WriteLine($"\tFound Label {label.Name} with confidence {label.Confidence}");
                            imageLabels.Add(new ImageLabel { LabelName = label.Name, LabelConfidence = label.Confidence });
                        }
                        else
                        {
                            Console.WriteLine($"\tSkipped label {label.Name} with confidence {label.Confidence} because confidence was less than 90%");
                        }
                    }

                    // create a new ImageObject instance to save in dynamo db
                    ImageObject newImageObject = new ImageObject
                    {
                        ImageUrl = $"https://{record.S3.Bucket.Name}.s3.amazonaws.com/" + record.S3.Object.Key,
                        ImageKey = record.S3.Object.Key.Replace("images/", ""),
                        UpdatedAt = DateTime.Now,
                        ThumbnailLink = $"https://{record.S3.Bucket.Name}.s3.amazonaws.com/thumbnails/thumb-" + record.S3.Object.Key.Replace("images/", ""),
                        Labels = imageLabels
                    };

                    // save the new item in dynamo db
                    await this.DynamoDbContext.SaveAsync(newImageObject);
                }
                return "Recogonized and Saved!!";
            } 
            catch (Exception e)
            {
                context.Logger.LogLine($"There was an error while finding image labels and/or saving it to dynamo db");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                throw;
            }
        }

        /// <summary>
        /// This method is called for every Generate Thumbnail Lambda invocation. 
        /// This method takes in an S3 event object and generates and saves 
        /// thumbnail to /thumbnails folder in same S3 bucket.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task<string> GenerateThumbnail(S3Event input, ILambdaContext context)
        {
            var s3Event = input.Records?[0].S3;
            if (s3Event == null)
            {
                return null;
            }

            try
            {
                var response = await this.S3Client.GetObjectMetadataAsync(s3Event.Bucket.Name, s3Event.Object.Key);

                // check if the file is of type image
                if (response.Headers.ContentType.StartsWith("image/"))
                {
                    using (GetObjectResponse getResponse = await S3Client.GetObjectAsync(
                        s3Event.Bucket.Name,
                        s3Event.Object.Key))
                    {
                        using (Stream responseStream = getResponse.ResponseStream)
                        {
                            using (StreamReader reader = new StreamReader(responseStream))
                            {
                                using (var memstream = new MemoryStream())
                                {
                                    var buffer = new byte[512];
                                    var bytesRead = default(int);
                                    while ((bytesRead = reader.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                                        memstream.Write(buffer, 0, bytesRead);

                                    // Get the image transformed as a thumbnail
                                    var thumnailresultImage = GcImagingOperations.GetConvertedImage(memstream.ToArray());

                                    // convert resulting base 64 image to byte array
                                    byte[] transformedImageBytes = Convert.FromBase64String(thumnailresultImage);

                                    // convert byte array to memory stream for sending it to S3
                                    MemoryStream transformedImageMemoryStream = new MemoryStream(transformedImageBytes);

                                    // replace the image/ directory to only get the object key
                                    string thumbnailName = s3Event.Object.Key.Replace("images/", "");

                                    // put the objects with public read enabled so that I can access it through public link
                                    PutObjectRequest putRequest = new PutObjectRequest()
                                    {
                                        BucketName = s3Event.Bucket.Name,
                                        Key = $"thumbnails/thumb-{thumbnailName}",
                                        ContentType = response.Headers.ContentType,
                                        CannedACL = S3CannedACL.PublicRead
                                    };
                                    // assign the thumbnail image stream to s3 put request
                                    putRequest.InputStream = transformedImageMemoryStream;

                                    await S3Client.PutObjectAsync(putRequest);
                                }
                            }
                        }
                    }
                }
                return "Thumbnail Created!!";
            }
            catch (Exception e)
            {
                context.Logger.LogLine($"There was an error while creating thumbnail for the object: {s3Event.Object.Key}.");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                throw;
            }
        }
    }
}
