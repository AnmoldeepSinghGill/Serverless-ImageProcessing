using Amazon.DynamoDBv2.DataModel;
using System;
using System.Collections.Generic;
using System.Text;

/*
 * Created By:
 * Name: Anmoldeep Singh Gill
 * Student Number: 301044883
 */

namespace Gill_AWSServerlessApp.Models
{
    [DynamoDBTable("Lab04Images")]
    class ImageObject
    {
        [DynamoDBHashKey]
        public string ImageUrl { get; set; }

        [DynamoDBProperty]
        public string ImageKey { get; set; }

        [DynamoDBProperty]
        public DateTime UpdatedAt { get; set; }

        [DynamoDBProperty]
        public string ThumbnailLink { get; set; }

        [DynamoDBProperty]
        public List<ImageLabel> Labels { get; set; }
    }
}
